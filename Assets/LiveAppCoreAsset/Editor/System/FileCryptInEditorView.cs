using Cysharp.Threading.Tasks;
using SFB;
using StudioSystemSDK.Application;
using StudioSystemSDK.Domain;
using StudioSystemSDK.Infrastructure;
using System.IO;
using UnityEditor;

namespace LiveAppCore.Editor.View
{
    /// <summary>
    /// 파일 암호화/복호화 처리 관련 View
    /// </summary>
    /// <remarks>Editor 메뉴와의 연계를 위한 Editor 한정 View</remarks>
    public class FileCryptInEditorView
    {
        private const string CryptoKeyAssetPath = "Assets/LiveAppCoreAsset/Submodules/StudioSystemSDK/Infrastructure/CryptoKeySetting.asset";
        private const string DecryptedFileExtension = "_decrypted.txt";
        private static string[] _extensionList = new string[]{ "bin", "json", "txt"};
        public static async UniTask<bool> ExecuteFileEncryption()
        {
            try
            {
                var sfb = new StandaloneFileBrowserEditor();
                var fileSystemDomain = new FileSystemInfrastructure();
                var fileContext = new FileSystemContext(fileSystemDomain);
                var cryptoProcessor = new AESCryptoProcessor();
                var cryptoContext = new CryptoContext(cryptoProcessor );

                var setting = AssetDatabase.LoadAssetAtPath<CryptoKeySetting>( CryptoKeyAssetPath );
                if( setting == null )
                {
                    UnityEngine.Debug.LogError( $"[FileCryptView] CryptoKeySettings asset not found. Path: {CryptoKeyAssetPath}" );
                    return false;
                }

                var loadPath = sfb.OpenFilePanel( "Select BInary File", 
                    string.Empty, 
                    ConvertToFilter( _extensionList ), 
                    false )[0];

                var rawText = await fileContext.ReadBinaryFile( loadPath );
                UnityEngine.Debug.Log( $"View(Raw) : {rawText}, Key : {setting.CryptoKey}" );
                var encryptedText = await cryptoContext.ConvertToEncryptedData( rawText, setting.CryptoKey );
                UnityEngine.Debug.Log( $"View(Encrypted) : {encryptedText}" );
                return await fileContext.SaveBinaryFile(loadPath, encryptedText );
            }
            catch( System.Exception ex )
            {
                UnityEngine.Debug.LogError( ex.Message );
                return false;
            }
        }
        public static async UniTask<bool> ExecuteFileDecryption()
        {
            try
            {
                var sfb = new StandaloneFileBrowserEditor();
                var fileSystemDomain = new FileSystemInfrastructure();
                var fileContext = new FileSystemContext(fileSystemDomain);
                var cryptoProcessor = new AESCryptoProcessor();
                var cryptoContext = new CryptoContext(cryptoProcessor );

                var setting = AssetDatabase.LoadAssetAtPath<CryptoKeySetting>( CryptoKeyAssetPath );
                if( setting == null )
                {
                    UnityEngine.Debug.LogError( $"[FileCryptView] CryptoKeySettings asset not found. Path: {CryptoKeyAssetPath}" );
                    return false;
                }

                var loadPath = sfb.OpenFilePanel( "Select BInary File",
                    string.Empty,
                    ConvertToFilter( _extensionList ),
                    false )[0]; 
                var tempPath = Path.Combine(Path.GetDirectoryName(loadPath), 
                    Path.GetFileNameWithoutExtension(loadPath));
                var savePath = $"{tempPath}{DecryptedFileExtension}";
                //var savePath = Path.Combine(tempPath, DecryptedFileExtension);

                var encryptedText = await fileContext.ReadBinaryFile( loadPath );
                UnityEngine.Debug.Log( $"View(Raw) : {encryptedText}" );
                var decryptedText = await cryptoContext.ConvertToDecryptedData( encryptedText, setting.CryptoKey );
                UnityEngine.Debug.Log( $"View(Decrypted) : {decryptedText}" );
                return await fileContext.SaveBinaryFile( savePath, decryptedText );
            }
            catch( System.Exception ex )
            {
                UnityEngine.Debug.LogError( ex.Message );
                return false;
            }
        }
        private static ExtensionFilter[] ConvertToFilter( string[] extensions )
        {
            return new[] { new ExtensionFilter( string.Join( ", ", extensions ), extensions ) };
        }
    }
}
