using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ObsAgent
{
    public sealed class ObsAgentOperations
    {
        private readonly Func<ObsAgentConfiguration> _configProvider;
        private readonly Action<string> _log;
        private readonly SemaphoreSlim _commandGate = new SemaphoreSlim(1, 1);

        public ObsAgentOperations( Func<ObsAgentConfiguration> configProvider, Action<string> log )
        {
            _configProvider = configProvider ?? throw new ArgumentNullException( nameof( configProvider ) );
            _log = log ?? ( _ => { } );
        }

        public bool IsObsRunning()
        {
            try
            {
                foreach( string processName
                         in GetObsProcessNames() )
                {
                    Process[] processes = Process.GetProcessesByName( processName);
                    bool found = processes.Length > 0;
                    foreach( Process process in processes )
                    {
                        process.Dispose();
                    }

                    if( found )
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string[] GetObsProcessNames()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return new[] { "obs64" };
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            return new[] { "OBS", "obs" };
#else
            return Array.Empty<string>();
#endif
        }

        public async Task<AgentApiResponse> LaunchObsAsync( CancellationToken cancellationToken )
        {
            await _commandGate.WaitAsync( cancellationToken );

            try
            {
                ObsAgentConfiguration config = _configProvider().Clone();

                ValidateConfig( config );

                bool launched = await EnsureObsStartedAsync(config, cancellationToken);

                _log( launched ? "OBS 프로세스를 새로 실행했습니다." : "OBS가 이미 실행 중입니다." );

                using( var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken ) )
                {
                    timeout.CancelAfter( TimeSpan.FromSeconds( 15 ) );
                    using( var client = CreateWebSocketClient( config ) )
                    {
                        await client.ConnectAsync( timeout.Token );

                        // 장면은 이미 실행 중인 OBS에도 적용한다.
                        if( config.setSceneAfterLaunch && !string.IsNullOrWhiteSpace( config.defaultSceneName ) )
                        {
                            await SetSceneAsync( client, config.defaultSceneName, timeout.Token );
                        }

                        // 자동 녹화/방송은 새로 실행한 경우에만 수행한다.
                        // 이미 실행 중일 때는 별도 API로 명시적으로 제어한다.
                        if( launched && config.startRecordingAfterLaunch )
                        {
                            await client.RequestAsync( "StartRecord", timeout.Token );
                        }

                        if( launched && config.startStreamingAfterLaunch )
                        {
                            await client.RequestAsync( "StartStream", timeout.Token );
                        }

                        await client.CloseAsync( timeout.Token );
                    }
                }

                return AgentApiResponse.Ok( launched ? "OBS를 실행하고 초기 설정을 적용했습니다." : "실행 중인 OBS에 설정을 적용했습니다.", true,
                    launched );
            }
            catch( Exception exception )
            {
                _log( $"OBS 실행 실패: {exception.Message}" );
                return AgentApiResponse.Error( exception.Message, IsObsRunning() );
            }
            finally
            {
                _commandGate.Release();
            }
        }

        public Task<AgentApiResponse> TestConnectionAsync( CancellationToken cancellationToken )
        {
            return ExecuteSimpleRequestAsync( "GetVersion", "OBS WebSocket 연결에 성공했습니다.", cancellationToken );
        }

        public Task<AgentApiResponse> StartRecordAsync( CancellationToken cancellationToken )
        {
            return ExecuteSimpleRequestAsync( "StartRecord", "OBS 녹화를 시작했습니다.", cancellationToken );
        }

        public Task<AgentApiResponse> StopRecordAsync( CancellationToken cancellationToken )
        {
            return ExecuteSimpleRequestAsync( "StopRecord", "OBS 녹화를 중지했습니다.", cancellationToken );
        }

        public Task<AgentApiResponse> StartStreamAsync( CancellationToken cancellationToken )
        {
            return ExecuteSimpleRequestAsync( "StartStream", "OBS 방송을 시작했습니다.", cancellationToken );
        }

        public Task<AgentApiResponse> StopStreamAsync( CancellationToken cancellationToken )
        {
            return ExecuteSimpleRequestAsync( "StopStream", "OBS 방송을 중지했습니다.", cancellationToken );
        }

        public async Task<AgentApiResponse> SetSceneAsync( string sceneName, CancellationToken cancellationToken )
        {
            if( string.IsNullOrWhiteSpace( sceneName ) )
            {
                return AgentApiResponse.Error( "sceneName이 비어 있습니다.", IsObsRunning() );
            }
            if( sceneName.Length > 256 )
            {
                return AgentApiResponse.Error( "sceneName은 256자를 초과할 수 없습니다.", IsObsRunning() );
            }
            await _commandGate.WaitAsync( cancellationToken );
            try
            {
                ObsAgentConfiguration config = _configProvider().Clone();
                using( var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken ) )
                {
                    timeout.CancelAfter( TimeSpan.FromSeconds( 10 ) );
                    using( var client = CreateWebSocketClient( config ) )
                    {
                        await client.ConnectAsync( timeout.Token );
                        await SetSceneAsync( client, sceneName.Trim(), timeout.Token );
                        await client.CloseAsync( timeout.Token );
                    }
                }

                _log( $"OBS 장면 변경: {sceneName}" );

                return AgentApiResponse.Ok( $"OBS 장면을 '{sceneName}'(으)로 변경했습니다.", true );
            }
            catch( Exception exception )
            {
                _log( $"장면 변경 실패: {exception.Message}" );
                return AgentApiResponse.Error( exception.Message, IsObsRunning() );
            }
            finally
            {
                _commandGate.Release();
            }
        }

        private async Task<AgentApiResponse> ExecuteSimpleRequestAsync( string requestType, string successMessage, CancellationToken cancellationToken )
        {
            await _commandGate.WaitAsync( cancellationToken );

            try
            {
                ObsAgentConfiguration config = _configProvider().Clone();
                using( var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken ) )
                {
                    timeout.CancelAfter( TimeSpan.FromSeconds( 10 ) );
                    using( var client = CreateWebSocketClient( config ) )
                    {
                        await client.ConnectAsync( timeout.Token );
                        await client.RequestAsync( requestType, timeout.Token );
                        await client.CloseAsync( timeout.Token );
                    }
                }
                _log( successMessage );
                return AgentApiResponse.Ok( successMessage, true );
            }
            catch( Exception exception )
            {
                _log( $"{requestType} 실패: {exception.Message}" );
                return AgentApiResponse.Error( exception.Message, IsObsRunning() );
            }
            finally
            {
                _commandGate.Release();
            }
        }

        private static async Task SetSceneAsync( ObsWebSocketClient client, string sceneName, CancellationToken cancellationToken )
        {
            string requestDataJson = JsonUtility.ToJson( new ObsSceneRequestData { sceneName = sceneName });
            await client.RequestAsync( "SetCurrentProgramScene", requestDataJson, cancellationToken );
        }

        private ObsWebSocketClient CreateWebSocketClient( ObsAgentConfiguration config )
        {
            return new ObsWebSocketClient( "127.0.0.1", config.obsWebSocketPort, config.obsWebSocketPassword );
        }

        private async Task<bool> EnsureObsStartedAsync( ObsAgentConfiguration config, CancellationToken cancellationToken )
        {
            if( IsObsRunning() )
            {
                return false;
            }
            if( string.IsNullOrWhiteSpace( config.obsExecutablePath ) )
            {
                throw new InvalidOperationException( "OBS 실행 파일 경로가 비어 있습니다." );
            }
            if( !File.Exists( config.obsExecutablePath ) )
            {
                throw new FileNotFoundException( "OBS 실행 파일을 찾을 수 없습니다.", config.obsExecutablePath );
            }
            string workingDirectory = Path.GetDirectoryName(config.obsExecutablePath);
            if( string.IsNullOrWhiteSpace( workingDirectory ) )
            {
                throw new InvalidOperationException( "OBS Working Directory를 확인할 수 없습니다." );
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = config.obsExecutablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                Arguments = BuildLaunchArguments(config)
            };
            Process process = Process.Start(startInfo);
            if( process == null )
            {
                throw new InvalidOperationException( "OBS 프로세스를 시작하지 못했습니다." );
            }
            process.Dispose();
            _log( "OBS WebSocket 서버가 준비될 때까지 기다립니다." );

            bool ready = await WaitForPortAsync( "127.0.0.1", config.obsWebSocketPort, TimeSpan.FromSeconds(20), cancellationToken);

            if( !ready )
            {
                throw new TimeoutException( "OBS는 실행되었지만 WebSocket 서버가 준비되지 않았습니다. " +
                    "OBS의 도구 > WebSocket 서버 설정을 확인하세요. " +
                    "OBS가 비정상 종료된 뒤 안전 모드 확인창에서 대기 중인지도 확인하세요." );
            }

            return true;
        }

        private static string BuildLaunchArguments( ObsAgentConfiguration config )
        {
            var arguments = new List<string>();
            AddNamedArgument( arguments, "--profile", config.profileName );
            AddNamedArgument( arguments, "--collection", config.sceneCollectionName );
            AddNamedArgument( arguments, "--scene", config.defaultSceneName );

            if( config.minimizeToTray )
            {
                arguments.Add( "--minimize-to-tray" );
            }

            return string.Join( " ", arguments );
        }

        private static void AddNamedArgument( ICollection<string> arguments, string argumentName, string value )
        {
            if( string.IsNullOrWhiteSpace( value ) )
            {
                return;
            }
            arguments.Add( argumentName );
            arguments.Add( QuoteWindowsArgument( value.Trim() ) );
        }

        private static string QuoteWindowsArgument( string value )
        {
            if( string.IsNullOrEmpty( value ) )
            {
                return "\"\"";
            }
            bool needsQuotes = value.IndexOfAny( new[] { ' ', '\t', '\n', '\v', '"' }) >= 0;
            if( !needsQuotes )
            {
                return value;
            }

            var builder = new StringBuilder();
            builder.Append( '"' );

            int backslashCount = 0;

            foreach( char character in value )
            {
                if( character == '\\' )
                {
                    backslashCount++;
                    continue;
                }

                if( character == '"' )
                {
                    builder.Append( '\\', backslashCount * 2 + 1 );
                    builder.Append( '"' );
                    backslashCount = 0;
                    continue;
                }

                builder.Append( '\\', backslashCount );
                backslashCount = 0;
                builder.Append( character );
            }

            builder.Append( '\\', backslashCount * 2 );
            builder.Append( '"' );

            return builder.ToString();
        }

        private static async Task<bool> WaitForPortAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken )
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while( DateTime.UtcNow < deadline )
            {
                cancellationToken.ThrowIfCancellationRequested();
                using( var tcpClient = new TcpClient() )
                {
                    try
                    {
                        Task connectTask = tcpClient.ConnectAsync(host, port);
                        Task delayTask = Task.Delay( TimeSpan.FromMilliseconds(700), cancellationToken);
                        Task completed = await Task.WhenAny( connectTask, delayTask);
                        if( completed == connectTask )
                        {
                            await connectTask;
                            return true;
                        }
                    }
                    catch
                    {
                        // OBS 준비 중에는 접속 실패가 정상.
                    }
                }

                await Task.Delay( TimeSpan.FromMilliseconds( 300 ), cancellationToken );
            }

            return false;
        }

        private static void ValidateConfig( ObsAgentConfiguration config )
        {
            if( config.obsWebSocketPort < 1 ||
                config.obsWebSocketPort > 65535 )
            {
                throw new InvalidOperationException( "OBS WebSocket 포트가 올바르지 않습니다." );
            }
        }
    }
}
