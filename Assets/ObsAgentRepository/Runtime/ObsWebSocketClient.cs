using System;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ObsAgent
{
    public sealed class ObsWebSocketClient : IDisposable
    {
        private const int MaxMessageBytes = 1024 * 1024;

        private readonly Uri _uri;
        private readonly string _password;

        private ClientWebSocket _socket;

        public ObsWebSocketClient(
            string host,
            int port,
            string password )
        {
            _uri = new Uri( $"ws://{host}:{port}" );
            _password = password ?? string.Empty;
        }

        public async Task ConnectAsync( CancellationToken cancellationToken )
        {
            if( _socket != null )
            {
                throw new InvalidOperationException(
                    "OBS WebSocket 클라이언트가 이미 생성되었습니다." );
            }

            _socket = new ClientWebSocket();
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds( 15 );
            _socket.Options.AddSubProtocol( "obswebsocket.json" );

            await _socket.ConnectAsync( _uri, cancellationToken );

            string helloJson = await ReceiveTextAsync(cancellationToken);

            ObsHelloEnvelope hello =
                JsonUtility.FromJson<ObsHelloEnvelope>(helloJson);

            if( hello == null || hello.op != 0 || hello.d == null )
            {
                throw new InvalidOperationException(
                    $"OBS Hello 메시지가 올바르지 않습니다: {helloJson}" );
            }

            int rpcVersion = hello.d.rpcVersion > 0
                ? Math.Min(hello.d.rpcVersion, 1)
                : 1;

            string authentication = null;

            if( hello.d.authentication != null )
            {
                if( string.IsNullOrEmpty( _password ) )
                {
                    throw new InvalidOperationException(
                        "OBS WebSocket 인증이 활성화되어 있지만 " +
                        "비밀번호가 설정되지 않았습니다." );
                }

                authentication = CreateAuthentication(
                    _password,
                    hello.d.authentication.salt,
                    hello.d.authentication.challenge );
            }

            string identifyJson = CreateIdentifyJson(
                rpcVersion,
                authentication);

            await SendTextAsync( identifyJson, cancellationToken );

            string identifiedJson =
                await ReceiveTextAsync(cancellationToken);

            ObsOpEnvelope identified =
                JsonUtility.FromJson<ObsOpEnvelope>(identifiedJson);

            if( identified == null || identified.op != 2 )
            {
                throw new InvalidOperationException(
                    $"OBS Identify 인증에 실패했습니다: {identifiedJson}" );
            }
        }

        public async Task RequestAsync(
            string requestType,
            string requestDataJson,
            CancellationToken cancellationToken )
        {
            EnsureConnected();

            string requestId = Guid.NewGuid().ToString("N");
            string requestJson = CreateRequestJson(
                requestType,
                requestId,
                requestDataJson);

            await SendTextAsync( requestJson, cancellationToken );

            while( true )
            {
                string responseJson =
                    await ReceiveTextAsync(cancellationToken);

                ObsRequestResponseEnvelope response =
                    JsonUtility.FromJson<ObsRequestResponseEnvelope>(
                        responseJson);

                // 이벤트 등 다른 메시지가 들어왔다면 다음 메시지를 기다린다.
                if( response == null || response.op != 7 || response.d == null )
                {
                    continue;
                }

                if( !string.Equals(
                        response.d.requestId,
                        requestId,
                        StringComparison.Ordinal ) )
                {
                    continue;
                }

                if( response.d.requestStatus == null )
                {
                    throw new InvalidOperationException(
                        $"OBS 응답 상태가 없습니다: {responseJson}" );
                }

                if( !response.d.requestStatus.result )
                {
                    string comment =
                        response.d.requestStatus.comment
                        ?? "세부 오류 정보 없음";

                    throw new InvalidOperationException(
                        $"{requestType} 실패. " +
                        $"Code={response.d.requestStatus.code}, " +
                        $"Comment={comment}" );
                }

                return;
            }
        }

        public Task RequestAsync(
            string requestType,
            CancellationToken cancellationToken )
        {
            return RequestAsync(
                requestType,
                null,
                cancellationToken );
        }

        public async Task CloseAsync( CancellationToken cancellationToken )
        {
            if( _socket == null )
            {
                return;
            }

            if( _socket.State == WebSocketState.Open )
            {
                try
                {
                    await _socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Agent request completed",
                        cancellationToken );
                }
                catch
                {
                    // 종료 중 예외는 무시한다.
                }
            }
        }

        private async Task SendTextAsync(
            string message,
            CancellationToken cancellationToken )
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);

            await _socket.SendAsync(
                new ArraySegment<byte>( bytes ),
                WebSocketMessageType.Text,
                true,
                cancellationToken );
        }

        private async Task<string> ReceiveTextAsync(
            CancellationToken cancellationToken )
        {
            byte[] buffer = new byte[8192];

            using( var memory = new MemoryStream() )
            {
                while( true )
                {
                    WebSocketReceiveResult result =
                        await _socket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            cancellationToken);

                    if( result.MessageType == WebSocketMessageType.Close )
                    {
                        throw new WebSocketException(
                            $"OBS WebSocket 연결이 종료되었습니다. " +
                            $"Status={_socket.CloseStatus}, " +
                            $"Description={_socket.CloseStatusDescription}" );
                    }

                    if( result.MessageType != WebSocketMessageType.Text )
                    {
                        throw new WebSocketException(
                            "OBS에서 Text가 아닌 WebSocket 메시지를 받았습니다." );
                    }

                    memory.Write( buffer, 0, result.Count );

                    if( memory.Length > MaxMessageBytes )
                    {
                        throw new InvalidOperationException(
                            "OBS WebSocket 메시지가 허용 크기를 초과했습니다." );
                    }

                    if( result.EndOfMessage )
                    {
                        return Encoding.UTF8.GetString( memory.ToArray() );
                    }
                }
            }
        }

        private void EnsureConnected()
        {
            if( _socket == null ||
                _socket.State != WebSocketState.Open )
            {
                throw new InvalidOperationException(
                    "OBS WebSocket이 연결되어 있지 않습니다." );
            }
        }

        private static string CreateIdentifyJson(
            int rpcVersion,
            string authentication )
        {
            var builder = new StringBuilder();

            builder.Append( "{\"op\":1,\"d\":{" );
            builder.Append( "\"rpcVersion\":" );
            builder.Append( rpcVersion );
            builder.Append( ",\"eventSubscriptions\":0" );

            if( !string.IsNullOrEmpty( authentication ) )
            {
                builder.Append( ",\"authentication\":\"" );
                builder.Append( EscapeJson( authentication ) );
                builder.Append( "\"" );
            }

            builder.Append( "}}" );

            return builder.ToString();
        }

        private static string CreateRequestJson(
            string requestType,
            string requestId,
            string requestDataJson )
        {
            var builder = new StringBuilder();

            builder.Append( "{\"op\":6,\"d\":{" );
            builder.Append( "\"requestType\":\"" );
            builder.Append( EscapeJson( requestType ) );
            builder.Append( "\",\"requestId\":\"" );
            builder.Append( EscapeJson( requestId ) );
            builder.Append( "\"" );

            if( !string.IsNullOrWhiteSpace( requestDataJson ) )
            {
                builder.Append( ",\"requestData\":" );
                builder.Append( requestDataJson );
            }

            builder.Append( "}}" );

            return builder.ToString();
        }

        private static string CreateAuthentication(
            string password,
            string salt,
            string challenge )
        {
            string secret = Sha256Base64(password + salt);
            return Sha256Base64( secret + challenge );
        }

        private static string Sha256Base64( string value )
        {
            byte[] source = Encoding.UTF8.GetBytes(value);

            using( SHA256 sha256 = SHA256.Create() )
            {
                byte[] hash = sha256.ComputeHash(source);
                return Convert.ToBase64String( hash );
            }
        }

        private static string EscapeJson( string value )
        {
            if( string.IsNullOrEmpty( value ) )
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length + 8);

            foreach( char character in value )
            {
                switch( character )
                {
                    case '\\':
                        builder.Append( "\\\\" );
                        break;

                    case '"':
                        builder.Append( "\\\"" );
                        break;

                    case '\r':
                        builder.Append( "\\r" );
                        break;

                    case '\n':
                        builder.Append( "\\n" );
                        break;

                    case '\t':
                        builder.Append( "\\t" );
                        break;

                    default:
                        builder.Append( character );
                        break;
                }
            }

            return builder.ToString();
        }

        public void Dispose()
        {
            _socket?.Dispose();
            _socket = null;
        }

        [Serializable]
        private sealed class ObsOpEnvelope
        {
            public int op;
        }

        [Serializable]
        private sealed class ObsHelloEnvelope
        {
            public int op;
            public ObsHelloData d;
        }

        [Serializable]
        private sealed class ObsHelloData
        {
            public int rpcVersion;
            public ObsAuthenticationData authentication;
        }

        [Serializable]
        private sealed class ObsAuthenticationData
        {
            public string challenge;
            public string salt;
        }

        [Serializable]
        private sealed class ObsRequestResponseEnvelope
        {
            public int op;
            public ObsRequestResponseData d;
        }

        [Serializable]
        private sealed class ObsRequestResponseData
        {
            public string requestType;
            public string requestId;
            public ObsRequestStatus requestStatus;
        }

        [Serializable]
        private sealed class ObsRequestStatus
        {
            public bool result;
            public int code;
            public string comment;
        }
    }
}
