using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ObsAgent
{
    public sealed class YoutubeOAuthClient : IDisposable
    {
        private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        private const string YoutubeScope = "https://www.googleapis.com/auth/youtube.force-ssl";

        private string _accessToken;
        private DateTime _accessTokenExpiresUtc;

        private readonly Func<ObsAgentConfiguration> _configProvider;
        private readonly YoutubeOAuthCredentialStore _credentialStore;
        private readonly Action<string> _log;
        private readonly HttpClient _httpClient = new HttpClient();

        public YoutubeOAuthClient( Func<ObsAgentConfiguration> configProvider, YoutubeOAuthCredentialStore credentialStore, Action<string> log )
        {
            _configProvider = configProvider;
            _credentialStore = credentialStore;
            _log = log ?? ( _ => { } );
        }

        public async Task<string> GetAccessTokenAsync( CancellationToken cancellationToken )
        {
            if( string.IsNullOrWhiteSpace( _accessToken ) == false && DateTime.UtcNow < _accessTokenExpiresUtc.AddMinutes( -1 ) )
            {
                return _accessToken;
            }
            ObsAgentConfiguration config = _configProvider().Clone();
            ValidateConfig( config );
            string refreshToken = _credentialStore.LoadRefreshToken();
            if( !string.IsNullOrWhiteSpace( refreshToken ) )
            {
                return await RefreshAsync( config, refreshToken, cancellationToken );
            }
            return await AuthorizeAsync( config, cancellationToken );
        }

        private async Task<string> AuthorizeAsync( ObsAgentConfiguration config, CancellationToken cancellationToken )
        {
            var listener = new TcpListener( IPAddress.Loopback, 0 );
            listener.Start();

            try
            {
                int port = ((IPEndPoint) listener.LocalEndpoint).Port;
                string redirectUri = $"http://127.0.0.1:{port}/";
                string state = CreateRandomState();

                string authUrl = AuthorizationEndpoint +
                    "?client_id=" + Escape( config.youtubeOAuthClientId ) +
                    "&redirect_uri=" + Escape( redirectUri ) +
                    "&response_type=code" +
                    "&scope=" +
                    Escape( YoutubeScope ) +
                    "&access_type=offline" +
                    "&prompt=consent" +
                    "&state=" +
                    Escape( state );

                _log( "YouTube 인증을 위해 브라우저를 엽니다." );
                OpenBrowser( authUrl );

                using( cancellationToken.Register( () => {
                    try
                    {
                        listener.Stop();
                    }
                    catch
                    {
                    }
                } ) )
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();

                    using( client )
                    using( NetworkStream stream = client.GetStream() )
                    {
                        string requestTarget = await ReadRequestTargetAsync( stream );
                        Uri callbackUri = new Uri( "http://127.0.0.1" + requestTarget );
                        Dictionary<string, string> query = ParseQuery( callbackUri.Query );
                        await WriteBrowserResponseAsync( stream );
                        if( !query.TryGetValue( "state", out string returnedState ) || returnedState != state )
                        {
                            throw new InvalidOperationException( "OAuth state 검증에 실패했습니다." );
                        }
                        if( query.TryGetValue( "error", out string error ) )
                        {
                            throw new InvalidOperationException( $"Google OAuth 오류: {error}" );
                        }
                        if( !query.TryGetValue( "code", out string code ) || string.IsNullOrWhiteSpace( code ) )
                        {
                            throw new InvalidOperationException( "OAuth Authorization Code가 없습니다." );
                        }
                        return await ExchangeAuthorizationCodeAsync( config, code, redirectUri, cancellationToken );
                    }
                }
            }
            finally
            {
                try
                {
                    listener.Stop();
                }
                catch
                {
                }
            }
        }

        private async Task<string> ExchangeAuthorizationCodeAsync( ObsAgentConfiguration config, string code, string redirectUri, CancellationToken cancellationToken )
        {
            var form = new Dictionary<string, string>
                {
                    { "client_id", config.youtubeOAuthClientId },
                    { "client_secret", config.youtubeOAuthClientSecret },
                    { "code", code },
                    { "redirect_uri", redirectUri },
                    { "grant_type", "authorization_code" }
                };

            using( var content = new FormUrlEncodedContent( form ) )
            using( HttpResponseMessage response = await _httpClient.PostAsync( TokenEndpoint, content, cancellationToken ) )
            {
                string json = await response.Content.ReadAsStringAsync();
                if( !response.IsSuccessStatusCode )
                {
                    throw new InvalidOperationException( $"OAuth Token 교환 실패: {json}" );
                }
                OAuthTokenResponse token = JsonUtility.FromJson<OAuthTokenResponse>( json );
                if( token == null || string.IsNullOrWhiteSpace( token.access_token ) )
                {
                    throw new InvalidOperationException( "Access Token을 받지 못했습니다." );
                }
                if( !string.IsNullOrWhiteSpace( token.refresh_token ) )
                {
                    _credentialStore.SaveRefreshToken( token.refresh_token );
                }
                StoreAccessToken( token );
                return _accessToken;
            }
        }

        private async Task<string> RefreshAsync( ObsAgentConfiguration config, string refreshToken, CancellationToken cancellationToken )
        {
            var form = new Dictionary<string, string>
                {
                    { "client_id", config.youtubeOAuthClientId },
                    { "client_secret", config.youtubeOAuthClientSecret },
                    { "refresh_token", refreshToken },
                    { "grant_type", "refresh_token" }
                };

            using( var content =new FormUrlEncodedContent( form ) )
            using( HttpResponseMessage response = await _httpClient.PostAsync( TokenEndpoint, content, cancellationToken ) )
            {
                string json = await response.Content.ReadAsStringAsync();
                if( !response.IsSuccessStatusCode )
                {
                    throw new InvalidOperationException( $"OAuth Refresh 실패: {json}" );
                }
                OAuthTokenResponse token = JsonUtility.FromJson<OAuthTokenResponse>( json );
                StoreAccessToken( token );
                return _accessToken;
            }
        }

        private void StoreAccessToken( OAuthTokenResponse token )
        {
            if( token == null || string.IsNullOrWhiteSpace( token.access_token ) )
            {
                throw new InvalidOperationException( "Access Token이 비어 있습니다." );
            }
            _accessToken = token.access_token;
            int expiresIn = Mathf.Max( 60, token.expires_in );
            _accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds( expiresIn );
        }

        private static async Task<string> ReadRequestTargetAsync( NetworkStream stream )
        {
            using( var reader = new StreamReader( stream, Encoding.ASCII, false, 4096, true ) )
            {
                string firstLine = await reader.ReadLineAsync();
                if( string.IsNullOrWhiteSpace( firstLine ) )
                {
                    throw new InvalidOperationException( "OAuth Callback 요청이 비어 있습니다." );
                }
                string[] parts = firstLine.Split( ' ' );
                if( parts.Length < 2 )
                {
                    throw new InvalidOperationException( "OAuth Callback 요청이 올바르지 않습니다." );
                }
                string line;
                do
                {
                    line = await reader.ReadLineAsync();
                }
                while( line != null && line.Length > 0 );
                return parts[1];
            }
        }

        private static async Task WriteBrowserResponseAsync( NetworkStream stream )
        {
            const string body = "<html><body>" + "<h2>YouTube 인증 완료</h2>" + "<p>이 창을 닫고 앱으로 돌아가세요.</p>" + "</body></html>";
            byte[] bodyBytes = Encoding.UTF8.GetBytes( body );

            string headers =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes( headers );
            await stream.WriteAsync( headerBytes, 0, headerBytes.Length );
            await stream.WriteAsync( bodyBytes, 0, bodyBytes.Length );
        }

        private static void OpenBrowser( string url )
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            Process.Start( new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                } );
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            Process.Start( new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    Arguments = $"\"{url}\"",
                    UseShellExecute = false
                } );
#else
            throw new PlatformNotSupportedException();
#endif
        }

        private static string CreateRandomState()
        {
            byte[] bytes = new byte[16];
            using( RandomNumberGenerator random = RandomNumberGenerator.Create() )
            {
                random.GetBytes( bytes );
            }

            return Convert.ToBase64String( bytes )
                .TrimEnd( '=' )
                .Replace( '+', '-' )
                .Replace( '/', '_' );
        }

        private static Dictionary<string, string> ParseQuery( string query )
        {
            var result = new Dictionary<string, string>( StringComparer.Ordinal );
            string source = query?.TrimStart( '?' ) ?? string.Empty;

            foreach( string pair in source.Split( '&' ) )
            {
                if( string.IsNullOrWhiteSpace( pair ) )
                {
                    continue;
                }
                int separator = pair.IndexOf( '=' );
                string key = separator >= 0 ? pair.Substring( 0, separator ) : pair;
                string value = separator >= 0 ? pair.Substring( separator + 1 ) : string.Empty;
                result[ Uri.UnescapeDataString( key )] = Uri.UnescapeDataString( value );
            }
            return result;
        }

        private static string Escape( string value ) 
            => Uri.EscapeDataString( value ?? string.Empty );

        private static void ValidateConfig( ObsAgentConfiguration config )
        {
            if( string.IsNullOrWhiteSpace( config.youtubeOAuthClientId ) )
            {
                throw new InvalidOperationException( "YouTube OAuth Client ID가 없습니다." );
            }
            if( string.IsNullOrWhiteSpace( config.youtubeOAuthClientSecret ) )
            {
                throw new InvalidOperationException( "YouTube OAuth Client Secret이 없습니다." );
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }

        [Serializable]
        private sealed class OAuthTokenResponse
        {
            public string access_token;
            public string refresh_token;
            public int expires_in;
            public string token_type;
            public string scope;
        }
    }
}