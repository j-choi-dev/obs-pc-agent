using System;
using System.Threading;
using System.Threading.Tasks;

namespace ObsAgent
{
    public sealed class YoutubeLiveCoordinator : IDisposable
    {
        private readonly object _stateGate = new object();

        private readonly Func<ObsAgentConfiguration> _configProvider;

        private readonly ObsAgentOperations _operations;

        private readonly YoutubeOAuthClient _oauthClient;

        private readonly YoutubeLiveApiClient _youtubeApi;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();
        private YoutubeLiveState _state = YoutubeLiveState.IDLE;

        private string _message = "대기 중";

        private YoutubePreparedSession _prepared;

        public YoutubeLiveCoordinator( Func<ObsAgentConfiguration> configProvider, ObsAgentOperations operations, Action<string> log )
        {
            _configProvider = configProvider;
            _operations = operations;
            _log = log ?? ( _ => { } );

            var credentialStore = new YoutubeOAuthCredentialStore();
            _oauthClient = new YoutubeOAuthClient( _configProvider, credentialStore, _log );
            _youtubeApi = new YoutubeLiveApiClient( _oauthClient );
        }

        public YoutubeLiveStatusResponse GetStatus()
        {
            lock( _stateGate )
            {
                return YoutubeLiveStatusResponse.Create( _state != YoutubeLiveState.FAILED, _state, _message, _prepared?.BroadcastId ?? string.Empty );
            }
        }

        public YoutubeLiveStatusResponse BeginPrepare( YoutubeLivePrepareRequest request )
        {
            ValidatePrepareRequest( request );

            lock( _stateGate )
            {
                if( _state != YoutubeLiveState.IDLE && _state != YoutubeLiveState.FAILED )
                {
                    return YoutubeLiveStatusResponse.Create( false, _state, "현재 상태에서는 다시 준비할 수 없습니다.", _prepared?.BroadcastId );
                }
                _prepared = null;
                _state = YoutubeLiveState.PREPARING;
                _message = "방송 준비를 시작했습니다.";
            }
            _ = PrepareCoreAsync( CloneRequest( request ), _lifetimeCancellation.Token );
            return GetStatus();
        }

        public YoutubeLiveStatusResponse BeginStart()
        {
            lock( _stateGate )
            {
                if( _state != YoutubeLiveState.READY )
                {
                    return YoutubeLiveStatusResponse.Create( false, _state, "방송 준비가 완료되지 않았습니다.", _prepared?.BroadcastId );
                }
                _state = YoutubeLiveState.STARTING;
                _message = "OBS 송출을 시작합니다.";
            }

            _ = StartCoreAsync( _lifetimeCancellation.Token );

            return GetStatus();
        }

        public YoutubeLiveStatusResponse BeginStop()
        {
            lock( _stateGate )
            {
                if( _state != YoutubeLiveState.LIVE && _state != YoutubeLiveState.STARTING )
                {
                    return YoutubeLiveStatusResponse.Create( false, _state, "현재 방송 중이 아닙니다.", _prepared?.BroadcastId );
                }
                _state = YoutubeLiveState.STOPPING;
                _message = "방송을 종료합니다.";
            }
            _ = StopCoreAsync( _lifetimeCancellation.Token );

            return GetStatus();
        }

        private async Task PrepareCoreAsync( YoutubeLivePrepareRequest request, CancellationToken cancellationToken )
        {
            string broadcastId = null;

            try
            {
                SetStatus( YoutubeLiveState.PREPARING, "YouTube 인증을 확인합니다." );
                YoutubeLiveStreamInfo stream = await _youtubeApi.FindStreamByKeyAsync( request.streamKey, cancellationToken );
                SetStatus( YoutubeLiveState.PREPARING, "OBS 출력 설정을 적용합니다." );

                ObsAgentConfiguration config = _configProvider().Clone();

                AgentApiResponse obsResponse = await _operations.ConfigureYoutubeOutputAsync(
                            request.width,
                            request.height,
                            config.youtubeObsSceneName,
                            config.youtubeObsSourceName,
                            stream.RtmpsIngestionAddress,
                            request.streamKey,
                            cancellationToken );

                if( !obsResponse.success )
                {
                    throw new InvalidOperationException( obsResponse.message );
                }

                SetStatus( YoutubeLiveState.PREPARING, "YouTube Broadcast를 생성합니다." );

                broadcastId = await _youtubeApi.CreateBroadcastAsync( request.title, config.youtubePrivacyStatus, cancellationToken );
                SetStatus( YoutubeLiveState.PREPARING, "YouTube Stream을 Broadcast에 연결합니다." );
                await _youtubeApi.BindAsync( broadcastId, stream.StreamId, cancellationToken );

                lock( _stateGate )
                {
                    _prepared = new YoutubePreparedSession
                    { 
                        BroadcastId = broadcastId,
                        StreamId = stream.StreamId,
                        Title = request.title,
                        Width = request.width,
                        Height = request.height
                    };
                    _state = YoutubeLiveState.READY;
                    _message = "방송 준비가 완료되었습니다.";
                }
                _log( $"YouTube 방송 준비 완료. BroadcastId={broadcastId}" );
            }
            catch( Exception exception )
            {
                if( !string.IsNullOrWhiteSpace( broadcastId ) )
                {
                    try
                    {
                        await _youtubeApi.DeleteBroadcastAsync( broadcastId, CancellationToken.None );
                    }
                    catch
                    {
                    }
                }
                SetStatus( YoutubeLiveState.FAILED, exception.Message );
                _log( $"YouTube 방송 준비 실패: " + exception.Message );
            }
        }

        private async Task StartCoreAsync( CancellationToken cancellationToken )
        {
            YoutubePreparedSession session;
            lock( _stateGate )
            {
                session = _prepared;
            }

            if( session == null )
            {
                SetStatus( YoutubeLiveState.FAILED, "Prepared Session이 없습니다." );
                return;
            }

            bool obsStarted = false;

            try
            {
                AgentApiResponse response = await _operations.StartStreamAsync( cancellationToken );
                if( !response.success )
                {
                    throw new InvalidOperationException( response.message );
                }

                obsStarted = true;
                SetStatus( YoutubeLiveState.STARTING, "YouTube 서버가 영상 입력을 인식할 때까지 기다립니다." );
                await WaitForStreamActiveAsync( session.StreamId, cancellationToken );
                SetStatus( YoutubeLiveState.STARTING, "YouTube 방송을 LIVE 상태로 전환합니다." );
                await _youtubeApi.TransitionAsync( session.BroadcastId, "live", cancellationToken );
                await WaitForBroadcastStateAsync( session.BroadcastId, "live", cancellationToken );
                SetStatus( YoutubeLiveState.LIVE, "YouTube LIVE 방송 중입니다." );
            }
            catch( Exception exception )
            {
                if( obsStarted )
                {
                    try
                    {
                        await _operations.StopStreamAsync( CancellationToken.None );
                    }
                    catch
                    {
                    }
                }

                SetStatus(
                    YoutubeLiveState.READY,
                    $"방송 시작 실패: {exception.Message}" );
            }
        }

        private async Task StopCoreAsync( CancellationToken cancellationToken )
        {
            YoutubePreparedSession session;
            lock( _stateGate )
            {
                session = _prepared;
            }

            try
            {
                AgentApiResponse stopResponse = await _operations.StopStreamAsync( cancellationToken );
                if( !stopResponse.success )
                {
                    throw new InvalidOperationException( stopResponse.message );
                }

                if( session != null && string.IsNullOrWhiteSpace( session.BroadcastId ) == false )
                {
                    await _youtubeApi.TransitionAsync( session.BroadcastId, "complete", cancellationToken );
                }

                lock( _stateGate )
                {
                    _prepared = null;
                    _state = YoutubeLiveState.IDLE;
                    _message = "방송이 종료되었습니다.";
                }
            }
            catch( Exception exception )
            {
                SetStatus( YoutubeLiveState.FAILED, $"방송 종료 실패: {exception.Message}" ); 
            }
        }

        private async Task WaitForStreamActiveAsync( string streamId, CancellationToken cancellationToken )
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds( 60 );
            while( DateTime.UtcNow <deadline )
            {
                string status = await _youtubeApi.GetStreamStatusAsync( streamId, cancellationToken );

                if( string.Equals( status, "active", StringComparison.OrdinalIgnoreCase ) )
                {
                    return;
                }
                await Task.Delay( 1000, cancellationToken );
            }

            throw new TimeoutException( "YouTube가 60초 안에 Stream을 active 상태로 인식하지 못했습니다." );
        }

        private async Task WaitForBroadcastStateAsync( string broadcastId, string expected, CancellationToken cancellationToken )
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds( 60 );
            while( DateTime.UtcNow < deadline )
            {
                string status = await _youtubeApi.GetBroadcastStatusAsync( broadcastId, cancellationToken );
                if( string.Equals( status, expected, StringComparison.OrdinalIgnoreCase ) )
                {
                    return;
                }
                await Task.Delay( 1000, cancellationToken );
            }
            throw new TimeoutException( $"YouTube Broadcast가 {expected} 상태로 전환되지 않았습니다." );
        }

        private void SetStatus( YoutubeLiveState state, string message )
        {
            lock( _stateGate )
            {
                _state = state;
                _message = message ?? string.Empty;
            }
        }

        private static void ValidatePrepareRequest( YoutubeLivePrepareRequest request )
        {
            if( request == null )
            {
                throw new ArgumentNullException( nameof( request ) );
            }

            if( string.IsNullOrWhiteSpace( request.title ) )
            {
                throw new InvalidOperationException( "방송 제목이 비어 있습니다." );
            }

            if( request.title.Trim().Length > 100 )
            {
                throw new InvalidOperationException( "YouTube 방송 제목은 100자를 초과할 수 없습니다." );
            }

            bool validResolution = ( request.width == 1920 && request.height == 1080 ) || ( request.width == 1080 && request.height == 1920 );
            if( !validResolution )
            {
                throw new InvalidOperationException( "지원 해상도는 1920x1080 또는 1080x1920입니다." );
            }

            if( string.IsNullOrWhiteSpace( request.streamKey ) )
            {
                throw new InvalidOperationException( "YouTube Stream Key가 비어 있습니다." );
            }
        }

        private static YoutubeLivePrepareRequest CloneRequest( YoutubeLivePrepareRequest source )
        {
            return new YoutubeLivePrepareRequest
            {
                title = source.title.Trim(),
                width = source.width,
                height = source.height,
                streamKey = source.streamKey.Trim()
            };
        }

        public void Dispose()
        {
            _lifetimeCancellation.Cancel();
            _lifetimeCancellation.Dispose();

            _youtubeApi.Dispose();
            _oauthClient.Dispose();
        }
    }
}