using System;
using System.IO;
using UnityEngine;

namespace ObsAgent
{
    [Serializable]
    public sealed class ObsAgentConfiguration
    {
        [Header("Agent HTTP Server")]
        public int listenPort = 7443;
        public bool allowLanClients = true;
        public bool autoStartServer = true;
        public string agentToken = string.Empty;

        [Header("OBS Process")]
        public string obsExecutablePath =
            @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";

        public string profileName = string.Empty;
        public string sceneCollectionName = string.Empty;
        public string defaultSceneName = string.Empty;
        public bool minimizeToTray = true;

        [Header("OBS WebSocket")]
        public int obsWebSocketPort = 4455;
        public string obsWebSocketPassword = string.Empty;

        [Header("After New OBS Launch")]
        public bool setSceneAfterLaunch = true;
        public bool startRecordingAfterLaunch;
        public bool startStreamingAfterLaunch;

        public ObsAgentConfiguration Clone()
        {
            return new ObsAgentConfiguration
            {
                listenPort = listenPort,
                allowLanClients = allowLanClients,
                autoStartServer = autoStartServer,
                agentToken = agentToken,

                obsExecutablePath = obsExecutablePath,
                profileName = profileName,
                sceneCollectionName = sceneCollectionName,
                defaultSceneName = defaultSceneName,
                minimizeToTray = minimizeToTray,

                obsWebSocketPort = obsWebSocketPort,
                obsWebSocketPassword = obsWebSocketPassword,

                setSceneAfterLaunch = setSceneAfterLaunch,
                startRecordingAfterLaunch = startRecordingAfterLaunch,
                startStreamingAfterLaunch = startStreamingAfterLaunch
            };
        }
    }

    [Serializable]
    public sealed class AgentApiResponse
    {
        public bool success;
        public string message;
        public bool obsRunning;
        public bool launched;
        public string utcTime;

        public static AgentApiResponse Ok(
            string message,
            bool obsRunning,
            bool launched = false )
        {
            return new AgentApiResponse
            {
                success = true,
                message = message,
                obsRunning = obsRunning,
                launched = launched,
                utcTime = DateTime.UtcNow.ToString( "O" )
            };
        }

        public static AgentApiResponse Error(
            string message,
            bool obsRunning )
        {
            return new AgentApiResponse
            {
                success = false,
                message = message,
                obsRunning = obsRunning,
                launched = false,
                utcTime = DateTime.UtcNow.ToString( "O" )
            };
        }
    }

    [Serializable]
    public sealed class SceneCommandRequest
    {
        public string sceneName;
    }

    [Serializable]
    internal sealed class ObsSceneRequestData
    {
        public string sceneName;
    }

    public static class ObsAgentConfigStore
    {
        private const string FileName = "obs-agent-config.json";

        public static string ConfigPath =>
            Path.Combine( Application.persistentDataPath, FileName );

        public static ObsAgentConfiguration Load()
        {
            try
            {
                Debug.Log( ConfigPath );
                if( !File.Exists( ConfigPath ) )
                {
                    return new ObsAgentConfiguration();
                }
                string json = File.ReadAllText(ConfigPath);
                ObsAgentConfiguration config =
                    JsonUtility.FromJson<ObsAgentConfiguration>(json);

                return config ?? new ObsAgentConfiguration();
            }
            catch( Exception exception )
            {
                Debug.LogException( exception );
                return new ObsAgentConfiguration();
            }
        }

        public static void Save( ObsAgentConfiguration config )
        {
            if( config == null )
            {
                throw new ArgumentNullException( nameof( config ) );
            }

            string directory = Path.GetDirectoryName(ConfigPath);

            if( !string.IsNullOrWhiteSpace( directory ) )
            {
                Directory.CreateDirectory( directory );
            }

            string json = JsonUtility.ToJson(config, true);
            File.WriteAllText( ConfigPath, json );
        }
    }

    [Serializable]
    public sealed class VideoSessionRequest
    {
        public string sessionId;
    }

    [Serializable]
    public sealed class VideoSessionDescriptionRequest
    {
        public string sessionId;
        public string type;
        public string sdp;
    }

    [Serializable]
    public sealed class VideoSessionDescriptionResponse
    {
        public bool success;
        public string message;
        public bool hasValue;

        public string sessionId;
        public string type;
        public string sdp;

        public string utcTime;

        public static VideoSessionDescriptionResponse Empty( string sessionId, string type, string message )
        {
            return new VideoSessionDescriptionResponse
            {
                success = true,
                message = message,
                hasValue = false,

                sessionId = sessionId,
                type = type,
                sdp = string.Empty,

                utcTime = DateTime.UtcNow.ToString( "O" )
            };
        }

        public static VideoSessionDescriptionResponse Value( string sessionId, string type, string sdp, string message )
        {
            return new VideoSessionDescriptionResponse
            {
                success = true,
                message = message,
                hasValue = true,

                sessionId = sessionId,
                type = type,
                sdp = sdp,

                utcTime = DateTime.UtcNow.ToString( "O" )
            };
        }

        public static VideoSessionDescriptionResponse Error( string sessionId, string type, string message )
        {
            return new VideoSessionDescriptionResponse
            {
                success = false,
                message = message,
                hasValue = false,

                sessionId = sessionId,
                type = type,
                sdp = string.Empty,

                utcTime = DateTime.UtcNow.ToString( "O" )
            };
        }
    }
}
