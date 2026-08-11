#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ObsAgent
{
    public sealed class ObsAgentController : MonoBehaviour
    {
        private const int AgentPort = 7443;
        private const int ObsWebSocketPort = 4455;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private const string DefaultObsPath = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
        private const string CurrentPlatformName = "Windows";
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        private const string DefaultObsAppPath = "/Applications/OBS.app";
        private const string DefaultObsExecutablePathUppercase = "/Applications/OBS.app/Contents/MacOS/OBS";
        private const string DefaultObsExecutablePathLowercase = "/Applications/OBS.app/Contents/MacOS/obs";
        private const string CurrentPlatformName = "macOS";
#endif

        [Header("Required Input Fields")]
        [SerializeField] private TMP_InputField obsExecutablePathInput;
        [SerializeField] private TMP_InputField obsWebSocketPasswordInput;
        [SerializeField] private TMP_InputField agentTokenInput;

        [Header("Required Buttons")]
        [SerializeField] private Button applyAndStartButton;
        [SerializeField] private Button launchObsButton;
        [SerializeField] private Button testObsButton;

        [Header("Required Status")]
        [SerializeField] private TMP_Text statusText;

        private readonly object _configLock = new object();

        private readonly ConcurrentQueue<string> _pendingLogs = new ConcurrentQueue<string>();

        private ObsAgentConfiguration _currentConfig;
        private ObsAgentOperations _operations; 
        private ObsVideoSessionStore _videoSessionStore;
        private ObsAgentHttpServer _httpServer;

        private CancellationTokenSource _lifetimeCancellation;

        private string _lastMessage = "초기화 중";
        private float _nextStatusRefreshTime;
        private bool _isCommandRunning;
        private bool _isShuttingDown;

        private void Awake()
        {
            Application.runInBackground = true;
            Application.targetFrameRate = 15;

            if( !ValidateUiReferences() )
            {
                enabled = false;
                return;
            }

            _lifetimeCancellation = new CancellationTokenSource();
            _currentConfig = CreateCompactConfiguration( ObsAgentConfigStore.Load() );
            if( string.IsNullOrWhiteSpace( _currentConfig.agentToken ) )
            {
                _currentConfig.agentToken = GenerateSecureToken();
            }

            ObsAgentConfigStore.Save( _currentConfig );
            ApplyConfigurationToUi( _currentConfig );
            _operations = new ObsAgentOperations( GetConfigSnapshot, EnqueueLog );
            _videoSessionStore = new ObsVideoSessionStore();
            _httpServer = new ObsAgentHttpServer( GetConfigSnapshot, _operations, _videoSessionStore, EnqueueLog );
            RegisterButtonEvents();
            EnqueueLog( $"{CurrentPlatformName}용 OBS Agent를 초기화했습니다." );
        }

        private void Start()
        {
            // 실행 직후 Agent 서버를 자동으로 시작한다.
            StartOrRestartAgent();
        }

        private void Update()
        {
            FlushPendingLogs();

            if( Time.unscaledTime >= _nextStatusRefreshTime )
            {
                _nextStatusRefreshTime = Time.unscaledTime + 0.5f;
                RefreshStatusText();
            }
        }

        private void RegisterButtonEvents()
        {
            applyAndStartButton.onClick.AddListener( StartOrRestartAgent );
            launchObsButton.onClick.AddListener( LaunchObs );
            testObsButton.onClick.AddListener( TestObsConnection );
        }

        /// <summary>
        /// UI 입력값을 저장하고 Agent HTTP 서버를 재시작한다.
        /// </summary>
        private void StartOrRestartAgent()
        {
            try
            {
                ApplyUiToConfiguration();
                ObsAgentConfigStore.Save( GetConfigSnapshot() );
                if( _httpServer.IsRunning )
                {
                    _httpServer.Stop();
                }
                _videoSessionStore.Clear();
                _httpServer.Start();
                EnqueueLog( $"Agent 서버를 시작했습니다. Port={AgentPort}" );
            }
            catch( Exception exception )
            {
                EnqueueLog( $"Agent 서버 시작 실패: {exception.Message}" );
            }
            RefreshStatusText();
        }

        private void LaunchObs()
        {
            RunOperation( token => _operations.LaunchObsAsync( token ) );
        }

        private void TestObsConnection()
        {
            RunOperation( token => _operations.TestConnectionAsync( token ) );
        }

        private async void RunOperation( Func<CancellationToken, Task<AgentApiResponse>> operation )
        {
            if( _isCommandRunning )
            {
                EnqueueLog( "이전 작업이 아직 실행 중입니다." );
                return;
            }

            _isCommandRunning = true;
            SetButtonsInteractable( false );

            try
            {
                ApplyUiToConfiguration();
                ObsAgentConfigStore.Save( GetConfigSnapshot() );
                AgentApiResponse response = await operation( _lifetimeCancellation.Token);
                EnqueueLog( response.success ? response.message : $"실패: {response.message}" );
            }
            catch( OperationCanceledException )
            {
                EnqueueLog( "작업이 취소되었습니다." );
            }
            catch( Exception exception )
            {
                EnqueueLog( $"작업 중 오류: {exception.Message}" );
            }
            finally
            {
                _isCommandRunning = false;
                SetButtonsInteractable( true );
                RefreshStatusText();
            }
        }

        /// <summary>
        /// 기존 설정을 Compact Agent에 맞는 고정 설정으로 변환한다.
        /// </summary>
        private static ObsAgentConfiguration CreateCompactConfiguration( ObsAgentConfiguration loaded )
        {
            loaded ??= new ObsAgentConfiguration();
            return new ObsAgentConfiguration
            {
                listenPort = AgentPort,
                allowLanClients = true,
                autoStartServer = true,

                agentToken = loaded.agentToken ?? string.Empty,
                obsExecutablePath = string.IsNullOrWhiteSpace( loaded.obsExecutablePath ) ? GetDefaultObsPath() : NormalizeObsPath(loaded.obsExecutablePath ),
                obsWebSocketPort = ObsWebSocketPort,
                obsWebSocketPassword = loaded.obsWebSocketPassword ?? string.Empty,

                profileName = string.Empty,
                sceneCollectionName = string.Empty,
                defaultSceneName = string.Empty,
                minimizeToTray = DefaultMinimizeToTray,

                setSceneAfterLaunch = false,
                startRecordingAfterLaunch = false,
                startStreamingAfterLaunch = false
            };
        }

        private void ApplyConfigurationToUi( ObsAgentConfiguration config )
        {
            obsExecutablePathInput.SetTextWithoutNotify( config.obsExecutablePath );
            obsWebSocketPasswordInput.SetTextWithoutNotify( config.obsWebSocketPassword );
            agentTokenInput.SetTextWithoutNotify( config.agentToken );
        }

        private void ApplyUiToConfiguration()
        {
            string obsPath = NormalizeObsPath( obsExecutablePathInput.text);
            string agentToken = agentTokenInput.text.Trim();
            if( string.IsNullOrWhiteSpace( obsPath ) )
            {
                throw new InvalidOperationException( "OBS 실행 파일 경로를 입력하세요." );
            }

            if( string.IsNullOrWhiteSpace( agentToken ) || agentToken.Length < 16 )
            {
                throw new InvalidOperationException( "Agent Token은 16자 이상이어야 합니다." );
            }
            obsExecutablePathInput.SetTextWithoutNotify( obsPath );

            var updated = new ObsAgentConfiguration
            {
                listenPort = AgentPort,
                allowLanClients = true,
                autoStartServer = true,
                agentToken = agentToken,
                obsExecutablePath = obsPath,
                obsWebSocketPort = ObsWebSocketPort,
                obsWebSocketPassword = obsWebSocketPasswordInput.text,
                profileName = string.Empty,
                sceneCollectionName = string.Empty,
                defaultSceneName = string.Empty,
                minimizeToTray = DefaultMinimizeToTray,
                setSceneAfterLaunch = false,
                startRecordingAfterLaunch = false,
                startStreamingAfterLaunch = false
            };
            lock( _configLock )
            {
                _currentConfig = updated;
            }
        }

        private ObsAgentConfiguration GetConfigSnapshot()
        {
            lock( _configLock )
            {
                return _currentConfig.Clone();
            }
        }

        private void RefreshStatusText()
        {
            if( statusText == null )
            {
                return;
            }

            bool serverRunning = _httpServer != null && _httpServer.IsRunning;
            bool obsRunning = _operations != null && _operations.IsObsRunning();
            string endpoint = GetAgentEndpoint();

            statusText.text = $"Agent: {( serverRunning ? "RUNNING" : "STOPPED" )}\nOBS: {( obsRunning ? "RUNNING" : "STOPPED" )}\nEndpoint: {endpoint}\nOBS WebSocket: 127.0.0.1:{ObsWebSocketPort}\nStatus: {_lastMessage}";
        }

        private static string GetAgentEndpoint()
        {
            List<string> addresses = GetLocalIpv4Addresses();
            if( addresses.Count == 0 )
            {
                return $"http://<{CurrentPlatformName}-IP>:{AgentPort}";
            }
            return $"http://{addresses[0]}:{AgentPort}";
        }

        private static List<string> GetLocalIpv4Addresses()
        {
            var result = new List<string>();

            try
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach( NetworkInterface networkInterface in interfaces )
                {
                    if( networkInterface.OperationalStatus != OperationalStatus.Up )
                    {
                        continue;
                    }
                    if( networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback )
                    {
                        continue;
                    }
                    IPInterfaceProperties properties = networkInterface.GetIPProperties();
                    foreach( UnicastIPAddressInformation address in properties.UnicastAddresses )
                    {
                        IPAddress ipAddress = address.Address;
                        if( ipAddress.AddressFamily != AddressFamily.InterNetwork )
                        {
                            continue;
                        }
                        if( IPAddress.IsLoopback( ipAddress ) )
                        {
                            continue;
                        }

                        string ip = ipAddress.ToString();
                        if( ip.StartsWith( "169.254.", StringComparison.Ordinal ) )
                        {
                            continue;
                        }
                        if( !result.Contains( ip ) )
                        {
                            result.Add( ip );
                        }
                    }
                }
            }
            catch
            {
                // IP 표시 실패는 Agent 실행에 영향을 주지 않음.
            }
            return result;
        }

        private static string GenerateSecureToken()
        {
            byte[] bytes = new byte[32];
            using( RandomNumberGenerator random = RandomNumberGenerator.Create() )
            {
                random.GetBytes( bytes );
            }
            return Convert.ToBase64String( bytes ) .TrimEnd( '=' ) .Replace( '+', '-' ) .Replace( '/', '_' );
        }

        private void EnqueueLog( string message )
        {
            _pendingLogs.Enqueue( $"[{DateTime.Now:HH:mm:ss}] {message}" );
        }

        private void FlushPendingLogs()
        {
            while( _pendingLogs.TryDequeue( out string message ) )
            {
                _lastMessage = message;
                Debug.Log( message );
            }
        }

        private void SetButtonsInteractable( bool interactable )
        {
            applyAndStartButton.interactable = interactable;
            launchObsButton.interactable = interactable;
            testObsButton.interactable = interactable;
        }

        private bool ValidateUiReferences()
        {
            bool valid = obsExecutablePathInput != null &&
                obsWebSocketPasswordInput != null &&
                agentTokenInput != null &&
                applyAndStartButton != null &&
                launchObsButton != null &&
                testObsButton != null &&
                statusText != null;

            if( !valid )
            {
                Debug.LogError( "ObsAgentController의 필수 UI 참조가 Inspector에 연결되지 않았습니다." );
            }

            return valid;
        }

        private void Shutdown()
        {
            if( _isShuttingDown )
            {
                return;
            }

            _isShuttingDown = true;

            try
            {
                _lifetimeCancellation?.Cancel();
                _httpServer?.Stop(); 
                _videoSessionStore?.Clear();
            }
            catch( Exception exception )
            {
                Debug.LogException( exception );
            }
            finally
            {
                _lifetimeCancellation?.Dispose();
                _lifetimeCancellation = null;
            }
        }
        private static string GetDefaultObsPath()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return DefaultObsPath;
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            if (File.Exists(DefaultObsExecutablePathUppercase))
            {
                return DefaultObsExecutablePathUppercase;
            }
            if (File.Exists(DefaultObsExecutablePathLowercase))
            {
                return DefaultObsExecutablePathLowercase;
            }
            // 설치 여부를 아직 확인할 수 없을 때 보여줄 기본값
            return DefaultObsExecutablePathUppercase;
#else
            return string.Empty;
#endif
        }
        private static string NormalizeObsPath( string input )
        {
            if( string.IsNullOrWhiteSpace( input ) )
            {
                return string.Empty;
            }

            string path = input .Trim() .Trim('"');

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            if (path.EndsWith( ".app", StringComparison.OrdinalIgnoreCase))
            {
                string uppercaseExecutable = Path.Combine( path, "Contents", "MacOS", "OBS");
                if (File.Exists(uppercaseExecutable))
                {
                    return uppercaseExecutable;
                }

                string lowercaseExecutable = Path.Combine( path, "Contents", "MacOS", "obs");
                if (File.Exists(lowercaseExecutable))
                {
                    return lowercaseExecutable;
                }
                return uppercaseExecutable;
            }
#endif
            return path;
        }

        private static bool DefaultMinimizeToTray
        {
            get
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                return true;
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        return false;
#else
        return false;
#endif
            }
        }

        private void OnApplicationQuit()
        {
            Shutdown();
        }

        private void OnDestroy()
        {
            Shutdown();
        }
    }
}

#endif