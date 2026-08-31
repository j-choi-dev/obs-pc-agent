using Cysharp.Threading.Tasks;
using LiveAppCore.Editor.View;
using UnityEditor;

namespace LiveAppCore.Editor
{
    /// <summary>
    /// 암호화/복호화 관련 Unity Custom Menu
    /// </summary>
    public class CryptoMenuItem
    {
        private const string MENU_NAME_KEY_GENERATE = "LiveAppTool/File Crypto/Genrate Key";
        private const string MENU_NAME_ENCRYPTION = "LiveAppTool/File Crypto/Encryption";
        private const string MENU_NAME_DECRYPTION = "LiveAppTool/File Crypto/Decryption";


        [MenuItem( MENU_NAME_KEY_GENERATE, priority = 20 )]
        private static async UniTask<bool> GenerateKey()
        {
            var result = CryptoMenuView.GenerateKeyProcess();
            if( result == false )
            {
                UnityEngine.Debug.LogError( "FileEncrypt :: FAILED" );
            }
            return true;
        }

        [MenuItem( MENU_NAME_ENCRYPTION, priority = 21 )]
        private static async UniTask<bool> FileEncrypt()
        {
            var platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            var result = await FileCryptInEditorView.ExecuteFileEncryption();
            if(result == false)
            {
                UnityEngine.Debug.LogError( "FileEncrypt :: FAILED" );
            }
            return true;
        }

        [MenuItem( MENU_NAME_DECRYPTION, priority = 22 )]
        private static async UniTask<bool> FileDecrypt()
        {
            var platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            var result = await FileCryptInEditorView.ExecuteFileDecryption();
            if( result == false )
            {
                UnityEngine.Debug.LogError( "FileEncrypt :: FAILED" );
            }
            return true;
        }
    }
}
