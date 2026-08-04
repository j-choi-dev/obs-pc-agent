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
        private const int MaxRequestBytes = 64 * 1024;
        private const int MaxBodyBytes = 32 * 1024;

        private readonly Func<ObsAgentConfiguration> _configProvider;
        private readonly ObsAgentOperations _operations;
        private readonly Action<string> _log;

        private TcpListener _listener;
        private CancellationTokenSource _cancellation;
        private Task _acceptLoop;

        public bool IsRunning =>
            _listener != null &&
            _cancellation != null &&
            !_cancellation.IsCancellationRequested;

        public ObsAgentHttpServer( Func<ObsAgentConfiguration> configProvider, ObsAgentOperations operations, Action<string> log )
        {
            _configProvider = configProvider;
            _operations = operations;
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
                        HttpRequestData request = await ReadRequestAsync( stream, cancellationToken);
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
                await WriteEmptyAsync( stream, 204, cancellationToken );
                return;
            }

            if( request.Method == "GET" && path == "/health" )
            {
                await WriteJsonAsync( stream, 200, AgentApiResponse.Ok( "OBS Agent가 실행 중입니다.", _operations.IsObsRunning() ), cancellationToken );
                return;
            }

            if( !IsAuthorized( request ) )
            {
                await WriteJsonAsync( stream, 401, AgentApiResponse.Error( "Authorization Bearer Token이 올바르지 않습니다.", _operations.IsObsRunning() ),cancellationToken );
                return;
            }

            if( request.Method != "POST" )
            {
                await WriteJsonAsync( stream, 405, AgentApiResponse.Error( "지원하지 않는 HTTP Method입니다.", _operations.IsObsRunning() ), cancellationToken );
                return;
            }

            AgentApiResponse response;

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

                case "/api/obs/scene":
                    SceneCommandRequest command = ParseSceneCommand(request.Body);
                    response = await _operations.SetSceneAsync( command.sceneName, cancellationToken );
                    break;

                default:
                    await WriteJsonAsync( stream, 404,AgentApiResponse.Error( "API 경로를 찾을 수 없습니다.", _operations.IsObsRunning() ), cancellationToken );

                    return;
            }
            await WriteJsonAsync( stream, response.success ? 200 : 500, response, cancellationToken );
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

        private static async Task WriteJsonAsync( NetworkStream stream, int statusCode, AgentApiResponse response, CancellationToken cancellationToken )
        {
            string json = JsonUtility.ToJson(response);
            byte[] body = Encoding.UTF8.GetBytes(json);

            string headers = $"HTTP/1.1 {statusCode} {GetStatusText(statusCode)}\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n" +
                "Cache-Control: no-store\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Authorization, Content-Type\r\n" +
                "\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            await stream.WriteAsync( headerBytes, 0, headerBytes.Length, cancellationToken );
            await stream.WriteAsync( body, 0, body.Length, cancellationToken );
        }

        private static async Task WriteEmptyAsync( NetworkStream stream, int statusCode, CancellationToken cancellationToken )
        {
            string headers = $"HTTP/1.1 {statusCode} {GetStatusText(statusCode)}\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Authorization, Content-Type\r\n" +
                "\r\n";

            byte[] bytes = Encoding.ASCII.GetBytes(headers);
            await stream.WriteAsync( bytes, 0, bytes.Length, cancellationToken );
        }

        private static string NormalizePath( string path )
        {
            if( string.IsNullOrWhiteSpace( path ) )
            {
                return "/";
            }
            int queryIndex = path.IndexOf('?');
            return queryIndex >= 0
                ? path.Substring( 0, queryIndex )
                : path;
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

        private sealed class HttpRequestData
        {
            public string Method;
            public string Path;
            public string Body;
            public Dictionary<string, string> Headers;
        }
    }
}
