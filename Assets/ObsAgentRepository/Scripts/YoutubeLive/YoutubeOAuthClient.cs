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
        private const float TimeOutSeconds = 120f;

        private readonly Func<ObsAgentConfiguration> _configProvider;
        private readonly YoutubeOAuthCredentialStore _credentialStore;
        private readonly Action<string> _log;
        private readonly HttpClient _httpClient = new HttpClient();

        private string _accessToken;
        private DateTime _accessTokenExpiresUtc;

        public YoutubeOAuthClient( Func<ObsAgentConfiguration> configProvider, YoutubeOAuthCredentialStore credentialStore, Action<string> log )
        {
            _configProvider = configProvider ?? throw new ArgumentNullException( nameof( configProvider ) );
            _credentialStore = credentialStore ?? throw new ArgumentNullException( nameof( credentialStore ) );
            _log = log ?? ( _ => { } );
        }

        public async Task<string> GetAccessTokenAsync( CancellationToken cancellationToken )
        {
            if( !string.IsNullOrWhiteSpace( _accessToken ) && DateTime.UtcNow < _accessTokenExpiresUtc.AddMinutes( -1 ) )
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
            ValidateConfig( config );
            cancellationToken.ThrowIfCancellationRequested();
            var listener = new TcpListener( IPAddress.Loopback, 0 );
            listener.Start();

            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string redirectUri = $"http://127.0.0.1:{port}/";

                string codeVerifier = CreateCodeVerifier();
                string codeChallenge = CreateCodeChallenge( codeVerifier );
                string state = CreateState();

                string authorizationUrl = BuildAuthorizationUrl( config.youtubeOAuthClientId, redirectUri, state, codeChallenge );

                using( var oauthTimeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken ) )
                {
                    oauthTimeout.CancelAfter( TimeSpan.FromSeconds( TimeOutSeconds ) );

                    try
                    {
                        _log( "Google / YouTube OAuth 인증을 시작합니다." );
                        OpenBrowser( authorizationUrl );
                        string authorizationCode = await ReceiveAuthorizationCallbackAsync( listener, state, oauthTimeout.Token );
                        OAuthTokenResponse token = await ExchangeAuthorizationCodeAsync( config, authorizationCode, redirectUri, codeVerifier, oauthTimeout.Token );

                        if( !string.IsNullOrWhiteSpace( token.refresh_token ) )
                        {
                            _credentialStore.SaveRefreshToken( token.refresh_token );
                        }
                        StoreAccessToken( token );
                        _log( "Google / YouTube OAuth 인증이 완료되었습니다." );
                        return _accessToken;
                    }
                    catch( OperationCanceledException ) when( oauthTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested )
                    {
                        throw new TimeoutException( "Google / YouTube OAuth 인증 시간이 초과되었습니다. 다시 방송 준비를 실행하세요." );
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
                    // Listener 종료 중 예외는 무시한다.
                }
            }
        }

        private static string BuildAuthorizationUrl(
            string clientId,
            string redirectUri,
            string state,
            string codeChallenge )
        {
            return AuthorizationEndpoint
                + "?client_id=" + Uri.EscapeDataString( clientId )
                + "&redirect_uri=" + Uri.EscapeDataString( redirectUri )
                + "&response_type=code"
                + "&scope=" + Uri.EscapeDataString( YoutubeScope )
                + "&access_type=offline"
                + "&prompt=consent"
                + "&state=" + Uri.EscapeDataString( state )
                + "&code_challenge=" + Uri.EscapeDataString( codeChallenge )
                + "&code_challenge_method=S256";
        }

        private static string CreateCodeVerifier()
        {
            byte[] bytes = new byte[32];

            using( RandomNumberGenerator random = RandomNumberGenerator.Create() )
            {
                random.GetBytes( bytes );
            }

            return Base64UrlEncode( bytes );
        }

        private static string CreateCodeChallenge( string codeVerifier )
        {
            if( string.IsNullOrWhiteSpace( codeVerifier ) )
            {
                throw new ArgumentException( "PKCE Code Verifier가 비어 있습니다.", nameof( codeVerifier ) );
            }

            byte[] source = Encoding.ASCII.GetBytes(codeVerifier);

            using( SHA256 sha256 = SHA256.Create() )
            {
                return Base64UrlEncode( sha256.ComputeHash( source ) );
            }
        }

        private static string CreateState()
        {
            byte[] bytes = new byte[32];

            using( RandomNumberGenerator random = RandomNumberGenerator.Create() )
            {
                random.GetBytes( bytes );
            }

            return Base64UrlEncode( bytes );
        }

        private static string Base64UrlEncode( byte[] bytes )
        {
            return Convert.ToBase64String( bytes )
                .TrimEnd( '=' )
                .Replace( '+', '-' )
                .Replace( '/', '_' );
        }

        private static async Task<string> ReceiveAuthorizationCallbackAsync( TcpListener listener, string expectedState, CancellationToken cancellationToken )
        {
            TcpClient client = null;
            using( cancellationToken.Register( () =>
            {
                try
                {
                    listener.Stop();
                }
                catch
                {
                    // 취소 시 Listener 종료 예외는 무시한다.
                }
            } ) )
            {
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch( ObjectDisposedException ) when( cancellationToken.IsCancellationRequested )
                {
                    throw new OperationCanceledException( cancellationToken );
                }
                catch( SocketException ) when( cancellationToken.IsCancellationRequested )
                {
                    throw new OperationCanceledException( cancellationToken );
                }
            }

            using( client )
            using( NetworkStream stream = client.GetStream() )
            {
                string requestTarget = await ReadRequestTargetAsync(stream);

                Uri callbackUri = new Uri( "http://127.0.0.1" + requestTarget);

                Dictionary<string, string> query = ParseQuery( callbackUri.Query);

                if( query.TryGetValue( "error", out string error ) )
                {
                    await WriteBrowserResponseAsync( stream, false, "YouTube 인증이 취소되었거나 실패했습니다. OBS Agent를 확인하세요." );
                    throw new InvalidOperationException( $"Google OAuth 인증 실패: {error}" );
                }

                if( !query.TryGetValue( "state", out string returnedState ) || !string.Equals( returnedState, expectedState, StringComparison.Ordinal ) )
                {
                    await WriteBrowserResponseAsync( stream, false, "OAuth 보안 검증에 실패했습니다. OBS Agent를 확인하세요." );
                    throw new InvalidOperationException( "Google OAuth state 검증에 실패했습니다." );
                }

                if( !query.TryGetValue( "code", out string authorizationCode ) || string.IsNullOrWhiteSpace( authorizationCode ) )
                {
                    await WriteBrowserResponseAsync( stream, false, "Authorization Code를 받지 못했습니다. OBS Agent를 확인하세요." );
                    throw new InvalidOperationException( "Google OAuth Authorization Code가 없습니다." );
                }
                await WriteBrowserResponseAsync( stream, true, "YouTube 인증이 완료되었습니다. 이 창을 닫아도 됩니다." );

                return authorizationCode;
            }
        }

        private async Task<OAuthTokenResponse> ExchangeAuthorizationCodeAsync( ObsAgentConfiguration config, string authorizationCode, string redirectUri, string codeVerifier, CancellationToken cancellationToken )
        {
            var form = new Dictionary<string, string>
            {
                { "client_id", config.youtubeOAuthClientId },
                { "client_secret", config.youtubeOAuthClientSecret },
                { "code", authorizationCode },
                { "code_verifier", codeVerifier },
                { "redirect_uri", redirectUri },
                { "grant_type", "authorization_code" }
            };

            using( var content = new FormUrlEncodedContent( form ) )
            using( HttpResponseMessage response = await _httpClient.PostAsync( TokenEndpoint, content, cancellationToken ) )
            {
                string json = await response.Content.ReadAsStringAsync();

                if( !response.IsSuccessStatusCode )
                {
                    throw new InvalidOperationException( $"OAuth Token 교환 실패. HTTP={( int )response.StatusCode}, Response={json}" );
                }

                OAuthTokenResponse token = JsonUtility.FromJson<OAuthTokenResponse>(json);

                if( token == null || string.IsNullOrWhiteSpace( token.access_token ) )
                {
                    throw new InvalidOperationException( "Google OAuth Access Token을 받지 못했습니다." );
                }

                return token;
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

            using( var content = new FormUrlEncodedContent( form ) )
            using( HttpResponseMessage response = await _httpClient.PostAsync( TokenEndpoint, content, cancellationToken ) )
            {
                string json = await response.Content.ReadAsStringAsync();
                if( !response.IsSuccessStatusCode )
                {
                    throw new InvalidOperationException( $"OAuth Refresh 실패. HTTP={( int )response.StatusCode}, Response={json}" );
                }

                OAuthTokenResponse token = JsonUtility.FromJson<OAuthTokenResponse>(json);
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
            int expiresIn = Mathf.Max( 60, token.expires_in);
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

                string[] parts = firstLine.Split(' ');

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

        private static async Task WriteBrowserResponseAsync( NetworkStream stream, bool success, string message )
        {
            string title = success ? "YouTube 인증 완료" : "YouTube 인증 실패";

            string html = "<!doctype html>" +
                "<html lang=\"ko\">" +
                "<head>" +
                "<meta charset=\"utf-8\">" +
                $"<title>{title}</title>" +
                "</head>" +
                "<body>" +
                $"<h2>{title}</h2>" +
                $"<p>{WebUtility.HtmlEncode(message)}</p>" +
                "</body>" +
                "</html>";

            byte[] bodyBytes = Encoding.UTF8.GetBytes(html);

            string headers = "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);

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
            throw new PlatformNotSupportedException( "YouTube OAuth 브라우저 인증은 Windows/macOS OBS Agent에서만 지원합니다." );
#endif
        }

        private static Dictionary<string, string> ParseQuery( string query )
        {
            var result = new Dictionary<string, string>( StringComparer.Ordinal);

            string source = query?.TrimStart('?') ?? string.Empty;

            foreach( string pair in source.Split( '&' ) )
            {
                if( string.IsNullOrWhiteSpace( pair ) )
                {
                    continue;
                }

                int separator = pair.IndexOf('=');

                string key = separator >= 0 ? pair.Substring(0, separator) : pair;
                string value = separator >= 0 ? pair.Substring(separator + 1) : string.Empty;

                result[Uri.UnescapeDataString( key )] = Uri.UnescapeDataString( value );
            }
            return result;
        }

        private static void ValidateConfig( ObsAgentConfiguration config )
        {
            if( config == null )
            {
                throw new ArgumentNullException( nameof( config ) );
            }

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