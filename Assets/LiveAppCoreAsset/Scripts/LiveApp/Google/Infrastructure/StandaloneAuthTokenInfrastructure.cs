using Cysharp.Threading.Tasks;
using LiveAppCore.Google.Domain;
using SimpleJSON;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace LiveAppCore.Google.Infrastructure
{
    /// <summary>
    /// GoogleOAuthToken 취득을 위한 구현 클래스
    /// </summary>
    public class StandaloneAuthTokenInfrastructure : IGoogleAuthTokenDomain
    {
        private string _clientId;
        private string _clientSecret;
        private string _scope;
        private string _playerPrefsKey;

        private GoogleOAuthToken _cachedToken;

        public string Token => _cachedToken?.accessToken;

        public void SetAuthValue( GoogleOAuthSettings settings )
        {
            _clientId = settings.DesktopClientId;
            _clientSecret  = settings.DesktopClientSecret;
            _scope  = settings.SheetsReadonlyScope;
            _playerPrefsKey = $"GOOGLE_OAUTH_TOKEN_{_clientId}_{_scope}".GetHashCode().ToString();
        }

        public async UniTask<string> GetAccessTokenAsync( CancellationToken cancellationToken = default )
        {
            _cachedToken = LoadTokenFromPrefs();
            if( _cachedToken != null &&
                _cachedToken.HasValidAccessToken() )
            {
                return _cachedToken.accessToken;
            }

            if( _cachedToken != null &&
                string.IsNullOrEmpty( _cachedToken.refreshToken ) == false )
            {
                try
                {
                    _cachedToken = await RefreshAccessTokenAsync( _cachedToken.refreshToken, cancellationToken );

                    SaveTokenToPrefs( _cachedToken );
                    return _cachedToken.accessToken;
                }
                catch( Exception e )
                {
                    UnityEngine.Debug.LogError( $"[GoogleOAuth] Refresh failed. Re-auth required. {e.Message}" );
                    ClearPrefs();
                }
            }

            _cachedToken = await RunAuthorizationCodeFlowAsync( cancellationToken );
            SaveTokenToPrefs( _cachedToken );

            return _cachedToken.accessToken;
        }
        private GoogleOAuthToken LoadTokenFromPrefs()
        {
            var json = PlayerPrefs.GetString(_playerPrefsKey, string.Empty);

            if( string.IsNullOrEmpty( json ) )
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<GoogleOAuthToken>( json );
            }
            catch
            {
                return null;
            }
        }

        private async UniTask<GoogleOAuthToken> ExchangeCodeForTokenAsync(
            string code,
            string redirectUri,
            string codeVerifier,
            CancellationToken cancellationToken
        )
        {
            var form = new Dictionary<string, string>
            {
                { "client_id", _clientId },
                { "code", code },
                { "code_verifier", codeVerifier },
                { "grant_type", "authorization_code" },
                { "redirect_uri", redirectUri }
            };

            if( !string.IsNullOrWhiteSpace( _clientSecret ) )
            {
                form.Add( "client_secret", _clientSecret.Trim() );
            }

            var json = await PostFormAsync(OAuthConstValue.TokenEndpoint, form, cancellationToken);

            return ParseTokenResponse( json, keepRefreshToken: null );
        }

        private async UniTask<GoogleOAuthToken> RefreshAccessTokenAsync(
            string refreshToken,
            CancellationToken cancellationToken
        )
        {
            var form = new Dictionary<string, string>
            {
                { "client_id", _clientId },
                { "grant_type", "refresh_token" },
                { "refresh_token", refreshToken }
            };

            if( string.IsNullOrWhiteSpace( _clientSecret ) == false )
            {
                form.Add( "client_secret", _clientSecret.Trim() );
            }

            var json = await PostFormAsync(OAuthConstValue.TokenEndpoint, form, cancellationToken);

            return ParseTokenResponse( json, keepRefreshToken: refreshToken );
        }

        private async UniTask<GoogleOAuthToken> RunAuthorizationCodeFlowAsync( CancellationToken cancellationToken )
        {
            var port = GetRandomUnusedPort();
            var redirectUri = $"http://{OAuthConstValue.DefaultIP}:{port}/";

            var codeVerifier = CreateCodeVerifier();
            var codeChallenge = CreateCodeChallenge(codeVerifier);
            var state = CreateCodeVerifier();

            var listener = new TcpListener(IPAddress.Parse(OAuthConstValue.DefaultIP), port);

            var timeout = TimeSpan.FromSeconds(OAuthConstValue.TimeSpanSecond);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,timeoutCts.Token);

            try
            {
                listener.Start();

                var authUrl = BuildAuthorizationUrl(redirectUri,codeChallenge,state);

                Process browserProcess = OpenSystemBrowser(authUrl);
                var log =  browserProcess != null
                    ? $"browserProcess = {browserProcess.Id}"
                    : "browserProcess = null. Browser was opened via Application.OpenURL fallback.";
                UnityEngine.Debug.Log( log );

                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);

                var callbackTask = WaitForCallbackAsync(listener, waitCts.Token);

                AuthorizationWaitResult waitResult;

                if( IsTrackableBrowserProcess( browserProcess ) )
                {
                    var browserClosedTask = WaitForBrowserClosedAsync(browserProcess, waitCts.Token);

                    try
                    {
                        var (winArgumentIndex, callbackResult, browserClosedResult) = await UniTask.WhenAny( callbackTask, browserClosedTask );

                        waitResult = winArgumentIndex == 0
                            ? callbackResult
                            : browserClosedResult;

                        if( waitResult == null )
                        {
                            throw new InvalidOperationException( $"OAuth wait result is null. winArgumentIndex = {winArgumentIndex}" );
                        }

                        UnityEngine.Debug.Log( $"OAuth wait completed. winArgumentIndex = {winArgumentIndex}, status = {waitResult.Status}" );
                    }
                    finally
                    {
                        waitCts.Cancel();
                    }
                }
                else
                {
                    waitResult = await callbackTask;
                }

                if( waitResult.Status == AuthorizationWaitStatus.BrowserClosed )
                {
                    throw new OAuthBrowserClosedException();
                }

                TcpClient client = waitResult.Client;

                var requestText = await ReadHttpRequestAsync(client,linkedCts.Token);

                Dictionary<string, string> query = ParseQueryFromHttpRequest(requestText);

                if( query.TryGetValue( "error", out string error ) )
                {
                    await WriteHttpResponseAsync( client, OAuthResponseMessage.AUTH_FAILED, linkedCts.Token );
                    throw new Exception( $"OAuth authorization error: {error}" );
                }

                if( query.TryGetValue( "state", out string receivedState ) == false ||
                    receivedState != state )
                {
                    await WriteHttpResponseAsync( client, OAuthResponseMessage.INVALID_AUTH, linkedCts.Token );
                    throw new Exception( "Invalid OAuth state." );
                }

                if( query.TryGetValue( "code", out string code ) == false ||
                    string.IsNullOrEmpty( code ) )
                {
                    await WriteHttpResponseAsync( client, OAuthResponseMessage.CODE_NOT_FOUND, linkedCts.Token );
                    throw new Exception( "Authorization code not found." );
                }

                await WriteHttpResponseAsync( client, OAuthResponseMessage.COMPLETE, linkedCts.Token );

                return await ExchangeCodeForTokenAsync(
                    code,
                    redirectUri,
                    codeVerifier,
                    linkedCts.Token
                );
            }
            catch( OperationCanceledException )
            {
                if( timeoutCts.IsCancellationRequested &&
                    cancellationToken.IsCancellationRequested == false )
                {
                    throw new TimeoutException(
                        $"OAuth authorization timed out after {timeout.TotalSeconds} seconds."
                    );
                }

                throw;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static async UniTask<string> PostFormAsync(
            string url,
            Dictionary<string, string> form,
            CancellationToken cancellationToken
        )
        {
            var hasSecretKey = form.ContainsKey("client_secret");
            var secretValue = hasSecretKey ? form["client_secret"] : null;

            using UnityWebRequest request = UnityWebRequest.Post(url, form);
            request.timeout = 30;

            await request.SendWebRequest().ToUniTask( cancellationToken: cancellationToken );

            var body = request.downloadHandler != null
                ? request.downloadHandler.text
                : string.Empty;

            if( request.result != UnityWebRequest.Result.Success )
            {
                throw new Exception(
                    $"Token request failed. HTTP: {request.responseCode}, Error: {request.error}, Body: {body}"
                );
            }

            return body;
        }

        private GoogleOAuthToken ParseTokenResponse(
            string json,
            string keepRefreshToken
        )
        {
            var root = JSON.Parse(json);

            var accessToken = root["access_token"].Value;
            var refreshToken = root["refresh_token"].Value;

            if( string.IsNullOrEmpty( refreshToken ) )
            {
                refreshToken = keepRefreshToken;
            }

            var expiresIn = root["expires_in"].AsInt;
            var tokenType = root["token_type"].Value;
            var scope = root["scope"].Value;

            if( string.IsNullOrEmpty( accessToken ) )
            {
                throw new Exception( "access_token is empty." );
            }

            if( string.IsNullOrEmpty( refreshToken ) )
            {
                UnityEngine.Debug.LogError( "[GoogleOAuth] refresh_token is empty. Re-login may be required after access token expires." );
            }

            var expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn;

            return new GoogleOAuthToken
            {
                accessToken = accessToken,
                refreshToken = refreshToken,
                expiresAtUnixTime = expiresAt,
                tokenType = tokenType,
                scope = scope
            };
        }


        private void SaveTokenToPrefs( GoogleOAuthToken token )
        {
            string json = JsonUtility.ToJson(token);
            PlayerPrefs.SetString( _playerPrefsKey, json );
            PlayerPrefs.Save();
        }

        public void ClearAllPrefs()
        {
            PlayerPrefs.DeleteAll();
        }

        private void ClearPrefs()
        {
            PlayerPrefs.DeleteKey( _playerPrefsKey );
            PlayerPrefs.Save();
        }

        private static int GetRandomUnusedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string CreateCodeVerifier()
        {
            var bytes = new byte[64];

            using RandomNumberGenerator rng = RandomNumberGenerator.Create();
            rng.GetBytes( bytes );

            return Base64UrlEncode( bytes );
        }

        private static string CreateCodeChallenge( string codeVerifier )
        {
            using var sha256 = SHA256.Create();

            var bytes = Encoding.ASCII.GetBytes(codeVerifier);
            var hash = sha256.ComputeHash(bytes);

            return Base64UrlEncode( hash );
        }

        private static string Base64UrlEncode( byte[] bytes )
        {
            return Convert.ToBase64String( bytes )
                .TrimEnd( '=' )
                .Replace( '+', '-' )
                .Replace( '/', '_' );
        }

        private static Process OpenSystemBrowser( string url )
        {
            try
            {
                return Process.Start( new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                } );
            }
            catch
            {
                UnityEngine.Application.OpenURL( url );
                return null;
            }
        }

        private static async UniTask WriteHttpResponseAsync(
            TcpClient client,
            string html,
            CancellationToken cancellationToken
        )
        {
            var htmlBytes = Encoding.UTF8.GetBytes(html);

            string header =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {htmlBytes.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n";

            var headerBytes = Encoding.UTF8.GetBytes(header);

            NetworkStream stream = client.GetStream();

            await stream.WriteAsync( headerBytes, 0, headerBytes.Length, cancellationToken );
            await stream.WriteAsync( htmlBytes, 0, htmlBytes.Length, cancellationToken );

            client.Close();
        }


        private string BuildAuthorizationUrl(
            string redirectUri,
            string codeChallenge,
            string state
        )
        {
            UnityEngine.Debug.Log( $" _clientId = {_clientId}" );
            var query = new Dictionary<string, string>
            {
                { "client_id", _clientId },
                { "redirect_uri", redirectUri },
                { "response_type", "code" },
                { "scope", _scope },
                { "code_challenge", codeChallenge },
                { "code_challenge_method", "S256" },
                { "state", state },
                { "access_type", "offline" },
                { "prompt", "consent" }
            };

            return OAuthConstValue.AuthEndpoint + "?" + BuildFormUrlEncoded( query );
        }

        private static string BuildFormUrlEncoded( Dictionary<string, string> values )
        {
            var builder = new StringBuilder();

            foreach( KeyValuePair<string, string> pair in values )
            {
                if( builder.Length > 0 )
                {
                    builder.Append( "&" );
                }
                builder.Append( Uri.EscapeDataString( pair.Key ) );
                builder.Append( "=" );
                builder.Append( Uri.EscapeDataString( pair.Value ) );
            }

            return builder.ToString();
        }

        private static async UniTask<string> ReadHttpRequestAsync(
            TcpClient client,
            CancellationToken cancellationToken
        )
        {
            NetworkStream stream = client.GetStream();

            var buffer = new byte[8192];
            var length = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

            return Encoding.UTF8.GetString( buffer, 0, length );
        }

        private static Dictionary<string, string> ParseQueryFromHttpRequest( string requestText )
        {
            var result = new Dictionary<string, string>();

            var firstLine = requestText.Split('\n')[0].Trim();

            // 예: GET /?state=xxx&code=yyy HTTP/1.1
            var pathStart = firstLine.IndexOf(' ');
            var pathEnd = firstLine.LastIndexOf(' ');

            if( pathStart < 0 ||
                pathEnd <= pathStart )
            {
                return result;
            }

            var pathAndQuery = firstLine.Substring(pathStart + 1, pathEnd - pathStart - 1);

            var queryStart = pathAndQuery.IndexOf('?');

            if( queryStart < 0 )
            {
                return result;
            }

            var query = pathAndQuery.Substring(queryStart + 1);
            var pairs = query.Split('&');

            foreach( string pair in pairs )
            {
                if( string.IsNullOrEmpty( pair ) )
                {
                    continue;
                }

                var kv = pair.Split(new[] { '=' }, 2);

                var key = Uri.UnescapeDataString(kv[0]);
                var value = kv.Length > 1
                    ? Uri.UnescapeDataString(kv[1].Replace("+", " "))
                    : string.Empty;

                result[key] = value;
            }

            return result;
        }
        private static async UniTask<AuthorizationWaitResult> WaitForCallbackAsync( TcpListener listener, CancellationToken cancellationToken )
        {
            TcpClient client = await listener.AcceptTcpClientAsync()
                .AsUniTask()
                .AttachExternalCancellation(cancellationToken);

            return AuthorizationWaitResult.CallbackReceived( client );
        }

        private static async UniTask<AuthorizationWaitResult> WaitForBrowserClosedAsync(
            Process browserProcess,
            CancellationToken cancellationToken
        )
        {
            if( browserProcess == null )
            {
                await UniTask.Never( cancellationToken );
            }

            while( true )
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool hasExited;

                try
                {
                    browserProcess.Refresh();
                    hasExited = browserProcess.HasExited;
                }
                catch
                {
                    // 추적 불가 상태.
                    // 이걸 브라우저 종료로 처리하면 인증이 오탐 취소될 수 있으므로
                    // 그냥 callback/timeout을 기다리게 둔다.
                    await UniTask.Never( cancellationToken );
                    throw;
                }

                if( hasExited )
                {
                    return AuthorizationWaitResult.BrowserClosed();
                }

                await UniTask.Delay(
                    200,
                    cancellationToken: cancellationToken
                );
            }
        }

        private static bool IsTrackableBrowserProcess( Process process )
        {
            if( process == null )
            {
                return false;
            }

            try
            {
                process.Refresh();

                // 이미 종료된 프로세스라면 실제 브라우저가 아니라
                // URL 전달용 launcher process일 가능성이 높다.
                if( process.HasExited )
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
