using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ObsAgent
{
    public sealed class YoutubeLiveApiClient : IDisposable
    {
        private const string ApiBase = "https://www.googleapis.com/youtube/v3/";

        private readonly YoutubeOAuthClient _oauthClient;

        private readonly HttpClient _httpClient = new HttpClient();

        public YoutubeLiveApiClient( YoutubeOAuthClient oauthClient )
        {
            _oauthClient = oauthClient;
        }

        public async Task<YoutubeLiveStreamInfo> FindStreamByKeyAsync( string streamKey, CancellationToken cancellationToken )
        {
            string pageToken = string.Empty;

            do
            {
                string url = ApiBase + "liveStreams?part=cdn,status&mine=true&maxResults=50";

                if( !string.IsNullOrWhiteSpace( pageToken ) )
                {
                    url += "&pageToken=" + Uri.EscapeDataString( pageToken );
                }

                string json = await SendGetAsync( url, cancellationToken );

                LiveStreamListResponse response = JsonUtility.FromJson<LiveStreamListResponse>( json );

                if( response?.items != null )
                {
                    foreach( LiveStreamItem item in response.items )
                    {
                        string currentKey = item?.cdn?.ingestionInfo?.streamName;

                        if( !string.Equals( currentKey, streamKey, StringComparison.Ordinal ) )
                        {
                            continue;
                        }

                        string rtmps = item.cdn
                                .ingestionInfo
                                .rtmpsIngestionAddress;

                        if( string.IsNullOrWhiteSpace( rtmps ) )
                        {
                            throw new InvalidOperationException( "해당 YouTube Stream에서 RTMPS 주소를 얻지 못했습니다." );
                        }

                        return new YoutubeLiveStreamInfo { StreamId = item.id, RtmpsIngestionAddress = rtmps };
                    }
                }
                pageToken = response?.nextPageToken;
            }
            while( string.IsNullOrWhiteSpace( pageToken ) == false );

            throw new InvalidOperationException( "입력한 Stream Key와 일치하는 YouTube Live Stream을 찾지 못했습니다. 로그인한 채널과 Stream Key의 채널이 같은지 확인하세요." );
        }

        public async Task<string> CreateBroadcastAsync( string title, string privacyStatus, CancellationToken cancellationToken )
        {
            var request = new CreateBroadcastRequest
                {
                    snippet = new BroadcastSnippet { title = title, scheduledStartTime = DateTime.UtcNow.AddMinutes( 1 ).ToString( "O" ) },
                    status = new BroadcastInsertStatus { privacyStatus = privacyStatus },
                    contentDetails = new BroadcastContentDetails
                        {
                            enableAutoStart = false,
                            enableAutoStop = false,
                            enableDvr = true,
                            recordFromStart = true,
                            monitorStream = new MonitorStream { enableMonitorStream = false, broadcastStreamDelayMs = 0 }
                        }
                };

            string json = JsonUtility.ToJson( request );
            Debug.Log( $"YouTube Broadcast Insert Request: {json}" );
            string responseJson = await SendJsonAsync( HttpMethod.Post, ApiBase + "liveBroadcasts" + "?part=snippet,status,contentDetails", json, cancellationToken );
            BroadcastResponse response = JsonUtility.FromJson<BroadcastResponse>( responseJson );
            if( response == null || string.IsNullOrWhiteSpace( response.id ) )
            {
                throw new InvalidOperationException( "YouTube Broadcast ID를 받지 못했습니다." );
            }
            return response.id;
        }

        public async Task BindAsync( string broadcastId, string streamId, CancellationToken cancellationToken )
        {
            string url = ApiBase + "liveBroadcasts/bind?part=id,status,contentDetails" + 
                "&id=" + Uri.EscapeDataString( broadcastId ) + "&streamId=" + Uri.EscapeDataString( streamId );

            await SendJsonAsync( HttpMethod.Post, url, string.Empty, cancellationToken );
        }

        public async Task<string> GetStreamStatusAsync( string streamId, CancellationToken cancellationToken )
        {
            string json = await SendGetAsync( ApiBase + "liveStreams" + "?part=status&id=" + Uri.EscapeDataString( streamId ), cancellationToken );
            LiveStreamListResponse response = JsonUtility.FromJson<LiveStreamListResponse>( json );
            if( response?.items == null || response.items.Length == 0 )
            {
                return string.Empty;
            }

            return response.items[0]
                ?.status
                ?.streamStatus
                ?? string.Empty;
        }

        public async Task<string> GetBroadcastStatusAsync( string broadcastId, CancellationToken cancellationToken )
        {
            string json = await SendGetAsync( ApiBase + "liveBroadcasts" + "?part=status&id=" + Uri.EscapeDataString( broadcastId ), cancellationToken );
            BroadcastListResponse response = JsonUtility.FromJson<BroadcastListResponse>( json );
            if( response?.items == null || response.items.Length == 0 )
            {
                return string.Empty;
            }

            return response.items[0]
                ?.status
                ?.lifeCycleStatus
                ?? string.Empty;
        }

        public async Task TransitionAsync( string broadcastId, string status, CancellationToken cancellationToken )
        {
            string url = ApiBase + "liveBroadcasts/transition" +
                "?part=id,status" + "&broadcastStatus=" + Uri.EscapeDataString( status ) +
                "&id=" + Uri.EscapeDataString( broadcastId );

            await SendJsonAsync( HttpMethod.Post, url, string.Empty, cancellationToken );
        }

        public async Task DeleteBroadcastAsync( string broadcastId, CancellationToken cancellationToken )
        {
            string url = ApiBase + "liveBroadcasts?id=" + Uri.EscapeDataString( broadcastId );
            await SendJsonAsync( HttpMethod.Delete, url, null, cancellationToken );
        }

        private async Task<string> SendGetAsync( string url, CancellationToken cancellationToken )
        {
            return await SendJsonAsync( HttpMethod.Get, url, null, cancellationToken );
        }

        private async Task<string> SendJsonAsync( HttpMethod method, string url, string json, CancellationToken cancellationToken )
        {
            string accessToken = await _oauthClient.GetAccessTokenAsync( cancellationToken );
            using( var request = new HttpRequestMessage( method, url ) )
            {
                request.Headers.Authorization = new AuthenticationHeaderValue( "Bearer", accessToken );
                if( json != null )
                {
                    request.Content = new StringContent( json, Encoding.UTF8, "application/json" );
                }
                using( HttpResponseMessage response = await _httpClient.SendAsync( request, cancellationToken ) )
                {
                    string responseText = await response.Content.ReadAsStringAsync();
                    if( !response.IsSuccessStatusCode )
                    {
                        throw new InvalidOperationException( $"YouTube API 실패 " + $"HTTP={( int )response.StatusCode}\n" + responseText );
                    }
                    return responseText;
                }
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }

        [Serializable]
        private sealed class LiveStreamListResponse
        {
            public LiveStreamItem[] items;
            public string nextPageToken;
        }

        [Serializable]
        private sealed class LiveStreamItem
        {
            public string id;
            public LiveStreamCdn cdn;
            public LiveStreamStatus status;
        }

        [Serializable]
        private sealed class LiveStreamCdn
        {
            public LiveStreamIngestionInfo ingestionInfo;
        }

        [Serializable]
        private sealed class LiveStreamIngestionInfo
        {
            public string streamName;
            public string ingestionAddress;
            public string rtmpsIngestionAddress;
        }

        [Serializable]
        private sealed class LiveStreamStatus
        {
            public string streamStatus;
        }

        [Serializable]
        private sealed class CreateBroadcastRequest
        {
            public BroadcastSnippet snippet;
            public BroadcastInsertStatus status;
            public BroadcastContentDetails contentDetails;
        }

        [Serializable]
        private sealed class BroadcastSnippet
        {
            public string title;
            public string scheduledStartTime;
        }

        [Serializable]
        private sealed class BroadcastStatus
        {
            public string privacyStatus;
            public string lifeCycleStatus;
        }

        [Serializable]
        private sealed class BroadcastInsertStatus
        {
            public string privacyStatus;
        }

        [Serializable]
        private sealed class BroadcastResponseStatus
        {
            public string privacyStatus;
            public string lifeCycleStatus;
        }

        [Serializable]
        private sealed class BroadcastContentDetails
        {
            public bool enableAutoStart;
            public bool enableAutoStop;
            public bool enableDvr;
            public bool recordFromStart;

            public MonitorStream monitorStream;
        }

        [Serializable]
        private sealed class MonitorStream
        {
            public bool enableMonitorStream;
            public int broadcastStreamDelayMs;
        }

        [Serializable]
        private sealed class BroadcastResponse
        {
            public string id;
            public BroadcastResponseStatus status;
        }

        [Serializable]
        private sealed class BroadcastListResponse
        {
            public BroadcastResponse[] items;
        }
    }
}