#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX
using Cysharp.Threading.Tasks;
using SimpleJSON;
using StudioSystemSDK.Domain;
using StudioSystemSDK.Infrastructure;
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
        [SerializeField] private TMP_Text youtubeOAuthClientIdInput;
        [SerializeField] private TMP_Text endPointOutput;
        [SerializeField] private TMP_InputField obsExecutablePathInput;
        [SerializeField] private TMP_InputField obsWebSocketPasswordInput;
        [SerializeField] private TMP_InputField agentTokenInput;

        [Header("Required Buttons")]
        [SerializeField] private Button applyAndStartButton;
        [SerializeField] private Button launchObsButton;
        [SerializeField] private Button testObsButton;
        [SerializeField] private Button tokenRegenButton;
        [SerializeField] private Button copyButtonButton;

        [Header("Required Status")]
        [SerializeField] private TMP_Text statusText;

        private const string AuthFileName = "Auth.bin";

        [Header("Crypto")]
        [SerializeField] private CryptoKeySetting cryptoKeySetting;

        private readonly AESCryptoProcessor _cryptoProcessor = new AESCryptoProcessor();

        private readonly FileSerializer _fileSerializer = new FileSerializer();

        private readonly object _configLock = new object();

        private readonly ConcurrentQueue<string> _pendingLogs = new ConcurrentQueue<string>();

        private ObsAgentConfiguration _currentConfig;
        private ObsAgentOperations _operations;
        private ObsVideoSessionStore _videoSessionStore;
        private ObsAgentHttpServer _httpServer;
        private YoutubeLiveCoordinator _youtubeLiveCoordinator;

        private CancellationTokenSource _lifetimeCancellation;

        private string _lastMessage = "초기화 중";
        private float _nextStatusRefreshTime;
        private bool _isCommandRunning;
        private bool _isShuttingDown;
        private const int AgentTokenLength = 8;

        private void Awake()
        {
            Application.runInBackground = true;
            Application.targetFrameRate = 15;

            if( ValidateUiReferences() == false )
            {
                enabled = false;
                return;
            }

            _lifetimeCancellation = new CancellationTokenSource();
            _currentConfig = CreateCompactConfiguration( ObsAgentConfigStore.Load() );
            if( IsNeedRengeToken( _currentConfig.agentToken ) )
            {
                _currentConfig.agentToken = GenerateSecureToken();
                EnqueueLog( "저장된 Agent Token이 없거나 유효하지 않아 새 Token을 생성했습니다." );
            }
            ObsAgentConfigStore.Save( _currentConfig );
            ApplyConfigurationToUi( _currentConfig );
            _operations = new ObsAgentOperations( GetConfigSnapshot, EnqueueLog );
            _videoSessionStore = new ObsVideoSessionStore();
            _youtubeLiveCoordinator = new YoutubeLiveCoordinator( GetConfigSnapshot, _operations, EnqueueLog );
            _httpServer = new ObsAgentHttpServer( GetConfigSnapshot, _operations, _videoSessionStore, _youtubeLiveCoordinator, EnqueueLog );
            _youtubeLiveCoordinator = new YoutubeLiveCoordinator( GetConfigSnapshot, _operations, EnqueueLog );
            RegisterButtonEvents();
            EnqueueLog( $"{CurrentPlatformName}용 OBS Agent를 초기화했습니다." );

        }

        private async void Start()
        {
            bool authLoaded = await LoadAuthConfigurationAsync();

            if( !authLoaded )
            {
                EnqueueLog( "Auth.bin 로드에 실패했습니다. YouTube 기능은 사용할 수 없습니다." );
            }

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
        private bool IsNeedRengeToken( string token )
        {
            if( string.IsNullOrWhiteSpace( token ) )
            {
                return true;
            }
            string normalized = token.Trim();
            return normalized.Length < AgentTokenLength || normalized.Length > AgentTokenLength;
        }

        private void RegisterButtonEvents()
        {
            applyAndStartButton.onClick.AddListener( StartOrRestartAgent );
            launchObsButton.onClick.AddListener( LaunchObs );
            testObsButton.onClick.AddListener( TestObsConnection );
            tokenRegenButton.onClick.AddListener( RegenerateAgentToken );
            copyButtonButton.onClick.AddListener( CopyConnectionInfo );
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
                    endPointOutput.text =  string.Empty;
                }
                _videoSessionStore.Clear();
                _httpServer.Start();

                EnqueueLog( $"Agent 서버를 시작했습니다. Port={AgentPort}" );
            }
            catch( Exception exception )
            {
                endPointOutput.text =  string.Empty;
                EnqueueLog( $"Agent 서버 시작 실패: {exception.Message}" );
            }
            endPointOutput.text =  _httpServer != null && _httpServer.IsRunning ? GetAgentEndpoint() : string.Empty;
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

        private void RegenerateAgentToken()
        {
            try
            {
                string newToken = GenerateSecureToken();
                lock( _configLock )
                {
                    if( _currentConfig == null )
                    {
                        throw new InvalidOperationException( "Agent 설정이 초기화되지 않았습니다." );
                    }
                    _currentConfig.agentToken = newToken;
                }

                agentTokenInput.SetTextWithoutNotify( newToken );
                ObsAgentConfigStore.Save( GetConfigSnapshot() );
                EnqueueLog( "Agent Token을 새로 생성하고 저장했습니다." );
                RefreshStatusText();
            }
            catch( Exception exception )
            {
                EnqueueLog( $"Agent Token 재생성 실패: {exception.Message}" );
            }
        }
        private void CopyConnectionInfo()
        {
            try
            {
                string endpoint = endPointOutput != null ? endPointOutput.text.Trim() : string.Empty;
                if( string.IsNullOrWhiteSpace( endpoint ) )
                {
                    endpoint = GetAgentEndpoint();
                }
                string token;
                lock( _configLock )
                {
                    token = _currentConfig?.agentToken ?? string.Empty;
                }

                if( string.IsNullOrWhiteSpace( endpoint ) )
                {
                    throw new InvalidOperationException( "Agent Endpoint가 없습니다." );
                }

                if( string.IsNullOrWhiteSpace( token ) )
                {
                    throw new InvalidOperationException( "Agent Token이 없습니다." );
                }

                string copyText =            $"Endpoint: {endpoint}\nAgent Token: {token}";

                GUIUtility.systemCopyBuffer =                    copyText;

                EnqueueLog( "Endpoint와 Agent Token을 클립보드에 복사했습니다." );
            }
            catch( Exception exception )
            {
                EnqueueLog( $"연결 정보 복사 실패: {exception.Message}" );
            }
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
                obsExecutablePath = string.IsNullOrWhiteSpace( loaded.obsExecutablePath ) ? GetDefaultObsPath() : NormalizeObsPath( loaded.obsExecutablePath ),
                obsWebSocketPort = ObsWebSocketPort,
                obsWebSocketPassword = loaded.obsWebSocketPassword ?? string.Empty,

                profileName = string.Empty,
                sceneCollectionName = string.Empty,
                defaultSceneName = string.Empty,
                minimizeToTray = DefaultMinimizeToTray,

                setSceneAfterLaunch = false,
                startRecordingAfterLaunch = false,
                startStreamingAfterLaunch = false,

                youtubeOAuthClientId = loaded.youtubeOAuthClientId ?? string.Empty,

                youtubeOAuthClientSecret = loaded.youtubeOAuthClientSecret ?? string.Empty,

                youtubeObsSceneName = string.IsNullOrWhiteSpace( loaded.youtubeObsSceneName )
                    ? "YouTubeLive"
                    : loaded.youtubeObsSceneName,

                youtubeObsSourceName = string.IsNullOrWhiteSpace( loaded.youtubeObsSourceName )
                    ? "OBS Receiver"
                    : loaded.youtubeObsSourceName,

                youtubePrivacyStatus = string.IsNullOrWhiteSpace( loaded.youtubePrivacyStatus )
                    ? "unlisted"
                    : loaded.youtubePrivacyStatus,
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

            if( string.IsNullOrWhiteSpace( agentToken ) || agentToken.Length < AgentTokenLength )
            {
                throw new InvalidOperationException( $"Agent Token은 {AgentTokenLength}자 이상이어야 합니다." );
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
            byte[] bytes = new byte[6];

            using( RandomNumberGenerator random = RandomNumberGenerator.Create() )
            {
                random.GetBytes( bytes );
            }

            return Convert.ToBase64String( bytes )
                .Replace( '+', '-' )
                .Replace( '/', '_' );
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
            tokenRegenButton.interactable = interactable;
            copyButtonButton.interactable = true;
        }

        private bool ValidateUiReferences()
        {
            bool valid = endPointOutput != null &&
                obsExecutablePathInput != null &&
                obsWebSocketPasswordInput != null &&
                agentTokenInput != null &&
                applyAndStartButton != null &&
                launchObsButton != null &&
                testObsButton != null &&
                tokenRegenButton != null &&
                copyButtonButton != null &&
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
                _youtubeLiveCoordinator?.Dispose();
                _youtubeLiveCoordinator = null;
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
        private async UniTask<bool>
    LoadAuthConfigurationAsync()
        {
            try
            {
                if( cryptoKeySetting == null )
                {
                    throw new InvalidOperationException( "CryptoKeySetting이 Inspector에 연결되지 않았습니다." );
                }

                string cryptoKey = cryptoKeySetting.CryptoKey;

                if( string.IsNullOrWhiteSpace( cryptoKey ) )
                {
                    throw new InvalidOperationException( "Crypto Key가 비어 있습니다." );
                }

                string authPath =
            Path.Combine(
                SystemPathValue.ConfigOriginRoot,
                AuthFileName );

                EnqueueLog(
                    $"Auth 설정 파일을 읽습니다: {authPath}" );

                if( !File.Exists( authPath ) )
                {
                    throw new FileNotFoundException( "Auth.bin 파일을 찾을 수 없습니다.", authPath );
                }

                string encryptedText = await UniTask.RunOnThreadPool( () => File.ReadAllText( authPath ) );

                if( string.IsNullOrWhiteSpace( encryptedText ) )
                {
                    throw new InvalidOperationException( "Auth.bin 내용이 비어 있습니다." );
                }

                encryptedText = encryptedText.Trim();

                string decryptedJson = _cryptoProcessor.ConvertDecryptedString( encryptedText, cryptoKey );

                if( string.IsNullOrWhiteSpace( decryptedJson ) )
                {
                    throw new InvalidOperationException( "Auth.bin 복호화 결과가 비어 있습니다." );
                }

                ObsAgentAuthData authData = ParseAuthData( decryptedJson );

                if( authData == null )
                {
                    throw new InvalidOperationException( "Auth.bin의 JSON 데이터를 해석하지 못했습니다." );
                }

                string youtubeClientId = authData?.client_id?.Trim() ?? string.Empty;

                if( string.IsNullOrWhiteSpace( youtubeClientId ) )
                {
                    throw new InvalidOperationException( "Auth.bin에 YouTube OAuth Client ID가 없습니다." );
                }

                // 기본적인 Client ID 오입력 방지.
                if( !youtubeClientId.EndsWith( ".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase ) )
                {
                    throw new InvalidOperationException( "YouTube OAuth Client ID 형식이 올바르지 않습니다." );
                }

                // UI에 반영
                youtubeOAuthClientIdInput.text = youtubeClientId;

                // 현재 Agent Configuration에도 즉시 반영
                lock( _configLock )
                {
                    if( _currentConfig == null )
                    {
                        throw new InvalidOperationException( "Agent Configuration이 초기화되지 않았습니다." );
                    }

                    _currentConfig.youtubeOAuthClientId = youtubeClientId;
                }

                // Store에도 반영
                ObsAgentConfigStore.Save( GetConfigSnapshot() );

                EnqueueLog( "YouTube OAuth Client ID를 Auth.bin에서 불러왔습니다." );

                return true;
            }
            catch( CryptographicException exception )
            {
                EnqueueLog( "Auth.bin 복호화에 실패했습니다. Crypto Key 또는 파일 내용을 확인하세요." );
                Debug.LogException( exception );
                return false;
            }
            catch( FormatException exception )
            {
                EnqueueLog( "Auth.bin 암호화 데이터 형식이 올바르지 않습니다." );
                Debug.LogException( exception );
                return false;
            }
            catch( Exception exception )
            {
                EnqueueLog( $"Auth 설정 로드 실패: {exception.Message}" );
                Debug.LogException( exception );
                return false;
            }
        }
        private ObsAgentAuthData ParseAuthData(
    string decryptedJson )
        {
            if( string.IsNullOrWhiteSpace(
                    decryptedJson ) )
            {
                throw new InvalidOperationException(
                    "복호화된 Auth JSON이 비어 있습니다." );
            }

            JSONNode root =
        JSON.Parse(
            decryptedJson );

            if( root == null ||
                root["installed"] == null )
            {
                throw new InvalidOperationException(
                    "Auth JSON에 installed 정보가 없습니다." );
            }

            string installedJson =
        root["installed"].ToString();

            ObsAgentAuthData authData =
        JsonUtility.FromJson<ObsAgentAuthData>(
            installedJson );

            if( authData == null )
            {
                throw new InvalidOperationException(
                    "Auth JSON 파싱에 실패했습니다." );
            }

            return authData;
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