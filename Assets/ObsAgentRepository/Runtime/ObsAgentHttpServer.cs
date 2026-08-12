using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ObsAgent
{
    public sealed class ObsAgentHttpServer
    {
        private const int MaxRequestBytes = 384 * 1024;
        private const int MaxBodyBytes = 256 * 1024;

        private readonly Func<ObsAgentConfiguration> _configProvider;
        private readonly ObsAgentOperations _operations;
        private readonly ObsVideoSessionStore _videoSessionStore;
        private readonly Action<string> _log;

        private TcpListener _listener;
        private CancellationTokenSource _cancellation;
        private Task _acceptLoop;

        public bool IsRunning =>
            _listener != null &&
            _cancellation != null &&
            !_cancellation.IsCancellationRequested;

        public ObsAgentHttpServer( Func<ObsAgentConfiguration> configProvider, ObsAgentOperations operations, ObsVideoSessionStore videoSessionStore, Action<string> log )
        {
            _configProvider = configProvider;
            _operations = operations;
            _videoSessionStore = videoSessionStore;
            _log = log ?? ( _ => { } );
        }

        public void Start()
        {
            if( IsRunning )
            {
                return;
            }

            ObsAgentConfiguration config = _configProvider().Clone();

            if( config.listenPort < 1024 || config.listenPort > 65535 )
            {
                throw new InvalidOperationException( "Agent 포트는 1024~65535 범위로 설정하세요." );
            }

            if( string.IsNullOrWhiteSpace( config.agentToken ) ||
                config.agentToken.Length < 16 )
            {
                throw new InvalidOperationException( "Agent Token은 16자 이상이어야 합니다." );
            }

            IPAddress bindAddress = config.allowLanClients ? IPAddress.Any : IPAddress.Loopback;

            _cancellation = new CancellationTokenSource();
            _listener = new TcpListener( bindAddress, config.listenPort );

            _listener.Start();

            _acceptLoop = Task.Run( () => AcceptLoopAsync( _cancellation.Token ) );

            _log( $"Agent HTTP 서버 시작: {bindAddress}:{config.listenPort}" );
        }

        public void Stop()
        {
            if( !IsRunning )
            {
                return;
            }

            try
            {
                _cancellation.Cancel();
                _listener.Stop();
            }
            catch
            {
                // 종료 과정의 소켓 예외는 무시한다.
            }
            finally
            {
                _listener = null;
                _cancellation.Dispose();
                _cancellation = null;
                _acceptLoop = null;
            }
            _log( "Agent HTTP 서버를 중지했습니다." );
        }

        private async Task AcceptLoopAsync( CancellationToken cancellationToken )
        {
            while( !cancellationToken.IsCancellationRequested )
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run( () => HandleClientSafelyAsync( client, cancellationToken ), cancellationToken );
                }
                catch( ObjectDisposedException )
                {
                    break;
                }
                catch( SocketException ) when( cancellationToken.IsCancellationRequested )
                {
                    break;
                }
                catch( Exception exception )
                {
                    client?.Dispose();
                    _log( $"클라이언트 수락 실패: {exception.Message}" );

                    await Task.Delay( 300, cancellationToken );
                }
            }
        }

        private async Task HandleClientSafelyAsync( TcpClient client, CancellationToken cancellationToken )
        {
            using( client )
            {
                try
                {
                    client.NoDelay = true;

                    using( NetworkStream stream = client.GetStream() )
                    {
                        HttpRequestData request = await ReadRequestAsync( stream, cancellationToken );
                        IPEndPoint remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                        request.RemoteAddress = remoteEndPoint?.Address;
                        await RouteAsync( request, stream, cancellationToken );
                    }
                }
                catch( OperationCanceledException )
                {
                    // 서버 종료
                }
                catch( Exception exception )
                {
                    _log( $"HTTP 요청 처리 실패: {exception.Message}" );
                    try
                    {
                        using( NetworkStream stream = client.GetStream() )
                        {
                            await WriteJsonAsync( stream, 500, AgentApiResponse.Error( exception.Message, _operations.IsObsRunning() ), CancellationToken.None );
                        }
                    }
                    catch
                    {
                        // 응답 자체가 실패한 경우 무시한다.
                    }
                }
            }
        }

        private async Task RouteAsync( HttpRequestData request, NetworkStream stream, CancellationToken cancellationToken )
        {
            string path = NormalizePath(request.Path);
            if( request.Method == "OPTIONS" )
            {
                await WriteTextAsync( stream, 204, "text/plain; charset=utf-8", string.Empty, cancellationToken  );
                return;
            }

            if( request.Method == "GET" && path == "/health" )
            {
                await WriteJsonAsync( stream, 200, AgentApiResponse.Ok( "OBS Agent가 실행 중입니다.", _operations.IsObsRunning() ), cancellationToken );
                return;
            }
            if( request.Method == "GET" && path == "/video/receiver" )
            {
                if( !IsLoopbackRequest( request ) )
                {
                    await WriteJsonAsync( stream, 403, AgentApiResponse.Error( "Receiver 페이지는 PC 로컬에서만 접근할 수 있습니다.", _operations.IsObsRunning() ), cancellationToken );
                    return;
                }

                try
                {
                    string sessionId = GetRequiredQueryParameter( request.Path, "sessionId" );
                    ObsAgentConfiguration config = _configProvider().Clone();

                    string html = ObsBrowserReceiverPage.Build( sessionId, config.agentToken );
                    await WriteTextAsync( stream, 200, "text/html; charset=utf-8", html, cancellationToken );
                    return;
                }
                catch( InvalidOperationException exception )
                {
                    await WriteJsonAsync( stream, 400, AgentApiResponse.Error( exception.Message, _operations.IsObsRunning() ), cancellationToken );
                    return;
                }
            }

            if( !IsAuthorized( request ) )
            {
                await WriteJsonAsync( stream, 401, AgentApiResponse.Error( "Authorization Bearer Token이 올바르지 않습니다.", _operations.IsObsRunning() ), cancellationToken );
                return;
            }

            try
            {
                if( request.Method == "POST" && path == "/api/video/session/reset" )
                {
                    VideoSessionRequest body = ParseJsonBody<VideoSessionRequest>( request.Body );
                    string sessionId = ObsVideoSessionStore.NormalizeSessionId( body.sessionId );
                    _videoSessionStore.Reset( sessionId );
                    _log( $"WebRTC 세션 초기화: {sessionId}" );

                    await WriteJsonAsync( stream, 200, AgentApiResponse.Ok( "WebRTC 영상 세션을 초기화했습니다.", _operations.IsObsRunning() ), cancellationToken );
                    return;
                }

                if( request.Method == "POST" && path == "/api/video/offer" )
                {
                    VideoSessionDescriptionRequest body = ParseJsonBody< VideoSessionDescriptionRequest>( request.Body );
                    RequireDescriptionType( body.type, "offer" );
                    string sessionId = ObsVideoSessionStore.NormalizeSessionId( body.sessionId );

                    _videoSessionStore.SetOffer( sessionId, body.sdp );
                    _log( $"WebRTC Offer 등록: {sessionId}, SdpLength={body.sdp?.Length ?? 0}" );

                    await WriteJsonAsync( stream, 200, AgentApiResponse.Ok( "WebRTC Offer를 등록했습니다.", _operations.IsObsRunning() ), cancellationToken );
                    return;
                }

                if( request.Method == "GET" && path == "/api/video/offer" )
                {
                    string sessionId = GetRequiredQueryParameter( request.Path, "sessionId" );
                    sessionId = ObsVideoSessionStore .NormalizeSessionId( sessionId );
                    ObsVideoSessionStore.VideoSessionValue value = _videoSessionStore.GetOffer( sessionId );
                    VideoSessionDescriptionResponse response = value.HasValue 
                        ? VideoSessionDescriptionResponse.Value( sessionId, "offer", value.Sdp, "Offer가 준비되었습니다." ) 
                        : VideoSessionDescriptionResponse .Empty( sessionId, "offer", "아직 Offer가 없습니다." );
                    await WriteJsonAsync( stream, 200, response, cancellationToken );
                    return;
                }
                if( request.Method == "POST" && path == "/api/video/answer" )
                {
                    VideoSessionDescriptionRequest body = ParseJsonBody< VideoSessionDescriptionRequest>( request.Body );
                    RequireDescriptionType( body.type, "answer" );
                    string sessionId = ObsVideoSessionStore.NormalizeSessionId( body.sessionId );

                    _videoSessionStore.SetAnswer( sessionId, body.sdp );
                    _log( $"WebRTC Answer 등록: {sessionId}" );

                    await WriteJsonAsync( stream, 200, AgentApiResponse.Ok( "WebRTC Answer를 등록했습니다.", _operations.IsObsRunning() ),cancellationToken );
                    return;
                }
                if( request.Method == "GET" && path == "/api/video/answer" )
                {
                    string sessionId = GetRequiredQueryParameter( request.Path, "sessionId" );
                    sessionId = ObsVideoSessionStore.NormalizeSessionId( sessionId );
                    ObsVideoSessionStore.VideoSessionValue value = _videoSessionStore.GetAnswer( sessionId );
                    VideoSessionDescriptionResponse response = value.HasValue 
                        ? VideoSessionDescriptionResponse.Value( sessionId, "answer", value.Sdp, "Answer가 준비되었습니다." )
                        : VideoSessionDescriptionResponse .Empty( sessionId, "answer", "아직 Answer가 없습니다." );
                    await WriteJsonAsync( stream, 200, response, cancellationToken );
                    return;
                }

                if( request.Method == "POST" )
                {
                    AgentApiResponse response = null;
                    switch( path )
                    {
                        case "/api/obs/launch":
                            response = await _operations.LaunchObsAsync( cancellationToken );
                            break;

                        case "/api/obs/test":
                            response = await _operations.TestConnectionAsync( cancellationToken );
                            break;

                        case "/api/obs/record/start":
                            response = await _operations.StartRecordAsync( cancellationToken );
                            break;

                        case "/api/obs/record/stop":
                            response = await _operations.StopRecordAsync( cancellationToken );
                            break;

                        case "/api/obs/stream/start":
                            response = await _operations.StartStreamAsync( cancellationToken );
                            break;

                        case "/api/obs/stream/stop":
                            response = await _operations.StopStreamAsync( cancellationToken );
                            break;
                    }


                    if( response != null )
                    {
                        await WriteJsonAsync( stream, response.success ? 200 : 500, response, cancellationToken );
                        return;
                    }
                }
                if( IsKnownApiPath( path ) )
                {
                    await WriteJsonAsync( stream, 405, AgentApiResponse.Error( "해당 API에서 지원하지 않는 HTTP Method입니다.", _operations.IsObsRunning() ), cancellationToken );
                    return;
                }
                await WriteJsonAsync( stream, 404, AgentApiResponse.Error( "API 경로를 찾을 수 없습니다.", _operations.IsObsRunning() ), cancellationToken );
            }
            catch( InvalidOperationException exception )
            {
                await WriteJsonAsync( stream, 400, AgentApiResponse.Error( exception.Message, _operations.IsObsRunning() ), cancellationToken );
            }
        }

        private bool IsAuthorized( HttpRequestData request )
        {
            ObsAgentConfiguration config = _configProvider().Clone();
            if( !request.Headers.TryGetValue( "Authorization", out string authorization ) )
            {
                return false;
            }
            string expected = $"Bearer {config.agentToken}";
            return FixedTimeEquals( authorization, expected );
        }

        private static SceneCommandRequest ParseSceneCommand( string body )
        {
            if( string.IsNullOrWhiteSpace( body ) )
            {
                throw new InvalidOperationException( "장면 변경 요청 본문이 비어 있습니다." );
            }
            SceneCommandRequest command = JsonUtility.FromJson<SceneCommandRequest>(body);
            if( command == null || string.IsNullOrWhiteSpace( command.sceneName ) )
            {
                throw new InvalidOperationException( "sceneName이 필요합니다." );
            }
            return command;
        }

        private static async Task<HttpRequestData> ReadRequestAsync( NetworkStream stream, CancellationToken cancellationToken )
        {
            byte[] readBuffer = new byte[4096];
            using( var memory = new MemoryStream() )
            {
                int headerEnd = -1;
                int contentLength = 0;
                while( true )
                {
                    int read = await stream.ReadAsync( readBuffer, 0, readBuffer.Length, cancellationToken);
                    if( read <= 0 )
                    {
                        throw new IOException( "HTTP 요청 연결이 종료되었습니다." );
                    }
                    memory.Write( readBuffer, 0, read );
                    if( memory.Length > MaxRequestBytes )
                    {
                        throw new InvalidOperationException( "HTTP 요청이 허용 크기를 초과했습니다." );
                    }

                    byte[] current = memory.ToArray();

                    if( headerEnd < 0 )
                    {
                        headerEnd = FindHeaderEnd( current );

                        if( headerEnd >= 0 )
                        {
                            string headerText = Encoding.ASCII.GetString( current, 0, headerEnd);
                            contentLength = ParseContentLength( headerText );

                            if( contentLength > MaxBodyBytes )
                            {
                                throw new InvalidOperationException( "HTTP 요청 본문이 허용 크기를 초과했습니다." );
                            }
                        }
                    }

                    if( headerEnd >= 0 && memory.Length >= headerEnd + 4 + contentLength )
                    {
                        break;
                    }
                }

                byte[] requestBytes = memory.ToArray();
                string headersText = Encoding.ASCII.GetString( requestBytes, 0, headerEnd);

                HttpRequestData request = ParseHeaders(headersText);
                if( contentLength > 0 )
                {
                    request.Body = Encoding.UTF8.GetString( requestBytes, headerEnd + 4, contentLength );
                }
                else
                {
                    request.Body = string.Empty;
                }
                return request;
            }
        }

        private static HttpRequestData ParseHeaders( string headersText )
        {
            string[] lines = headersText.Split( new[] { "\r\n" }, StringSplitOptions.None);
            if( lines.Length == 0 )
            {
                throw new InvalidOperationException( "HTTP 요청 줄이 없습니다." );
            }
            string[] requestLine = lines[0].Split(' ');
            if( requestLine.Length < 2 )
            {
                throw new InvalidOperationException( "HTTP 요청 줄이 올바르지 않습니다." );
            }

            var request = new HttpRequestData
            {
                Method = requestLine[0].ToUpperInvariant(),
                Path = requestLine[1],
                Headers = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase)
            };

            for( int index = 1; index < lines.Length; index++ )
            {
                string line = lines[index];
                int separator = line.IndexOf(':');

                if( separator <= 0 )
                {
                    continue;
                }

                string name = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                request.Headers[name] = value;
            }
            return request;
        }

        private static int ParseContentLength( string headersText )
        {
            string[] lines = headersText.Split( new[] { "\r\n" }, StringSplitOptions.None);

            foreach( string line in lines )
            {
                int separator = line.IndexOf(':');

                if( separator <= 0 )
                {
                    continue;
                }

                string name = line.Substring(0, separator).Trim();

                if( !name.Equals( "Content-Length", StringComparison.OrdinalIgnoreCase ) )
                {
                    continue;
                }

                string value = line.Substring(separator + 1).Trim();

                if( !int.TryParse( value, out int length ) || length < 0 )
                {
                    throw new InvalidOperationException( "Content-Length가 올바르지 않습니다." );
                }

                return length;
            }

            return 0;
        }

        private static int FindHeaderEnd( byte[] bytes )
        {
            for( int index = 0; index <= bytes.Length - 4; index++ )
            {
                if( bytes[index] == '\r' &&
                    bytes[index + 1] == '\n' &&
                    bytes[index + 2] == '\r' &&
                    bytes[index + 3] == '\n' )
                {
                    return index;
                }
            }

            return -1;
        }
        private static async Task WriteJsonAsync<T>( NetworkStream stream, int statusCode, T response, CancellationToken cancellationToken )
        {
            string json = JsonUtility.ToJson(response);

            await WriteTextAsync( stream, statusCode, "application/json; charset=utf-8", json, cancellationToken );
        }

        private static async Task WriteTextAsync( NetworkStream stream, int statusCode, string contentType, string text, CancellationToken cancellationToken )
        {
            byte[] body = Encoding.UTF8.GetBytes( text ?? string.Empty );

            string headers = $"HTTP/1.1 {statusCode} " + 
                $"{GetStatusText(statusCode)}\r\n" + 
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n" +
                "Cache-Control: no-store\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: " +
                "GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: " +
                "Authorization, Content-Type\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes( headers );

            await stream.WriteAsync( headerBytes, 0, headerBytes.Length, cancellationToken );
            await stream.WriteAsync( body, 0, body.Length, cancellationToken );
        }

        private static string NormalizePath( string path )
        {
            if( string.IsNullOrWhiteSpace( path ) )
            {
                return "/";
            }
            int queryIndex = path.IndexOf('?');
            string result = queryIndex >= 0 ? path.Substring( 0, queryIndex ) : path;
            if( result.Length > 1 )
            {
                result = result.TrimEnd( '/' );
            }
            return result.ToLowerInvariant();
        }

        private static string GetStatusText( int statusCode )
        {
            switch( statusCode )
            {
                case 200:
                    return "OK";
                case 204:
                    return "No Content";
                case 400:
                    return "Bad Request";
                case 401:
                    return "Unauthorized";
                case 403:
                    return "Forbidden";
                case 404:
                    return "Not Found";
                case 405:
                    return "Method Not Allowed";
                case 500:
                    return "Internal Server Error";
                default:
                    return "Response";
            }
        }

        private static bool FixedTimeEquals( string first, string second )
        {
            if( first == null || second == null )
            {
                return false;
            }

            byte[] firstBytes = Encoding.UTF8.GetBytes(first);
            byte[] secondBytes = Encoding.UTF8.GetBytes(second);
            if( firstBytes.Length != secondBytes.Length )
            {
                return false;
            }

            int difference = 0;
            for( int index = 0; index < firstBytes.Length; index++ )
            {
                difference |=
                    firstBytes[index] ^
                    secondBytes[index];
            }

            return difference == 0;
        }

        private static void RequireDescriptionType( string actualType, string expectedType )
        {
            if( !string.Equals( actualType, expectedType, StringComparison.OrdinalIgnoreCase ) )
            {
                throw new InvalidOperationException( $"SDP type은 '{expectedType}'이어야 합니다." );
            }
        }

        private static string GetRequiredQueryParameter( string rawPath, string parameterName )
        {
            if( string.IsNullOrWhiteSpace( rawPath ) )
            {
                throw new InvalidOperationException( $"{parameterName} Query Parameter가 없습니다." );
            }

            int queryIndex = rawPath.IndexOf('?');

            if( queryIndex < 0 || queryIndex >= rawPath.Length - 1 )
            {
                throw new InvalidOperationException( $"{parameterName} Query Parameter가 없습니다." );
            }

            string query = rawPath.Substring( queryIndex + 1 );

            string[] pairs = query.Split('&');

            foreach( string pair in pairs )
            {
                if( string.IsNullOrWhiteSpace( pair ) )
                {
                    continue;
                }

                int separatorIndex = pair.IndexOf('=');
                string encodedName = separatorIndex >= 0
                    ? pair.Substring( 0, separatorIndex )
                    : pair;

                string encodedValue = separatorIndex >= 0 
                    ? pair.Substring( separatorIndex + 1 ) 
                    : string.Empty;

                string name = WebUtility.UrlDecode( encodedName );
                if( !string.Equals( name, parameterName, StringComparison.Ordinal ) )
                {
                    continue;
                }

                string value = WebUtility.UrlDecode( encodedValue );
                if( string.IsNullOrWhiteSpace( value ) )
                {
                    throw new InvalidOperationException( $"{parameterName} 값이 비어 있습니다." );
                }
                return value;
            }
            throw new InvalidOperationException( $"{parameterName} Query Parameter가 없습니다." );
        }

        private static T ParseJsonBody<T>( string body ) where T : class
        {
            if( string.IsNullOrWhiteSpace( body ) )
            {
                throw new InvalidOperationException( "요청 JSON 본문이 비어 있습니다." );
            }

            try
            {
                T result = JsonUtility.FromJson<T>( body );

                if( result == null )
                {
                    throw new InvalidOperationException( "요청 JSON을 해석하지 못했습니다." );
                }

                return result;
            }
            catch( InvalidOperationException )
            {
                throw;
            }
            catch( Exception exception )
            {
                throw new InvalidOperationException( "요청 JSON 형식이 올바르지 않습니다.", exception );
            }
        }

        private static bool IsLoopbackRequest( HttpRequestData request )
        {
            return request.RemoteAddress != null && IPAddress.IsLoopback( request.RemoteAddress );
        }

        private static bool IsKnownApiPath( string path )
        {
            switch( path )
            {
                case "/api/video/session/reset":
                case "/api/video/offer":
                case "/api/video/answer":
                case "/api/obs/launch":
                case "/api/obs/test":
                case "/api/obs/record/start":
                case "/api/obs/record/stop":
                case "/api/obs/stream/start":
                case "/api/obs/stream/stop":
                    return true;

                default:
                    return false;
            }
        }

        private sealed class HttpRequestData
        {
            public string Method;
            public string Path;
            public string Body;
            public Dictionary<string, string> Headers;
            public IPAddress RemoteAddress;
        }
    }
}
