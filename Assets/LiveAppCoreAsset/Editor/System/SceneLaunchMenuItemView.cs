using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiveAppCore.Editor
{
    [InitializeOnLoad]
    public static class SceneLaunchMenuItemView
    {
        private const string RunStudioCoreSceneMenuName = "LiveAppTool/HotKey/Run LiveAppTool &1";
        private const string OpenStudioCoreSceneMenuName = "LiveAppTool/HotKey/Open LiveAppTool #&1";
        private const string OpenStudioUISceneMenuName = "LiveAppTool/HotKey/Open LiveAppUI #&2";

        private static readonly string StudioCoreSceneName = "LiveAppCore";
        private static readonly string StudioUISceneName = "LiveAppUI";

        private const string RestoreScenePathKey = "SceneLaunchMenuItemView.RestoreScenePath";
        private const string ShouldRestoreSceneKey = "SceneLaunchMenuItemView.ShouldRestoreScene";

        static SceneLaunchMenuItemView()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        [MenuItem( RunStudioCoreSceneMenuName )]
        private static void RunStudioCoreScene()
        {
            if( EditorApplication.isPlayingOrWillChangePlaymode )
            {
                return;
            }

            Open( StudioCoreSceneName, true );

            // EditorApplication.isPlaying = true;
        }

        [MenuItem( OpenStudioCoreSceneMenuName )]
        private static void OpenLiveAppCoreScene()
        {
            if( EditorApplication.isPlayingOrWillChangePlaymode )
            {
                return;
            }

            Open( StudioCoreSceneName, true );
            EditorApplication.isPlaying = true;
        }

        [MenuItem( OpenStudioUISceneMenuName )]
        private static void OpenLiveAppUIScene()
        {
            if( EditorApplication.isPlayingOrWillChangePlaymode )
            {
                return;
            }

            Open( StudioUISceneName, true );

            // EditorApplication.isPlaying = true;
        }

        private static void Open( string sceneName, bool rememberCurrentScene )
        {
            var scene = EditorBuildSettings.scenes
            .FirstOrDefault(arg => arg.path.Contains(sceneName));

            if( scene == null )
            {
                Debug.LogError( $"Could not find Scene from Build Settings >>> sceneName: {sceneName}" );
                return;
            }

            if( string.IsNullOrEmpty( scene.path ) )
            {
                Debug.LogError( $"Scene path is NULL or Empty >>> sceneName: {sceneName}" );
                return;
            }

            if( !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo() )
            {
                return;
            }

            if( rememberCurrentScene )
            {
                SaveCurrentSceneForRestore();
            }

            EditorSceneManager.OpenScene( scene.path );
        }

        private static void SaveCurrentSceneForRestore()
        {
            Scene currentScene = SceneManager.GetActiveScene();

            if( !currentScene.IsValid() )
            {
                return;
            }

            if( string.IsNullOrEmpty( currentScene.path ) )
            {
                Debug.LogError( $"Scene path is NULL or Empty >>> sceneName: {currentScene.path}" );
                return;
            }

            SessionState.SetString( RestoreScenePathKey, currentScene.path );
            SessionState.SetBool( ShouldRestoreSceneKey, true );
        }

        private static void OnPlayModeStateChanged( PlayModeStateChange state )
        {
            if( state != PlayModeStateChange.EnteredEditMode )
            {
                return;
            }

            if( !SessionState.GetBool( ShouldRestoreSceneKey, false ) )
            {
                return;
            }

            string restoreScenePath = SessionState.GetString(RestoreScenePathKey, string.Empty);

            SessionState.EraseBool( ShouldRestoreSceneKey );
            SessionState.EraseString( RestoreScenePathKey );

            if( string.IsNullOrEmpty( restoreScenePath ) )
            {
                return;
            }

            if( !System.IO.File.Exists( restoreScenePath ) )
            {
                Debug.LogError( $"Could not find Prev Scene Path : {restoreScenePath}" );
                return;
            }

            // Play Á¾·á ÈÄ ¿ø·¡ ¾ÀÀ¸·Î º¹±Í
            EditorSceneManager.OpenScene( restoreScenePath );

            Debug.Log( $"Comeback Prev Scene : {restoreScenePath}" );
        }
    }
}