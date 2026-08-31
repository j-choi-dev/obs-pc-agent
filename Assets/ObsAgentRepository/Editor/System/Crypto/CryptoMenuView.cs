using StudioSystemSDK.Infrastructure;
using UnityEditor;
using UnityEngine;

namespace LiveAppCore.Editor
{
    /// <summary>
    /// 암호화/복호화 관련 Unity Custom Menu
    /// </summary>
    public class CryptoMenuView
    {
        private const string CryptoKeyAssetPath = "Assets/LiveAppCoreAsset/Submodules/StudioSystemSDK/Infrastructure/CryptoKeySetting.asset";
        private const string KeyPropertyName = "_cyryptoKey";

        public static bool GenerateKeyProcess()
        {
            bool confirmed = EditorUtility.DisplayDialog(
                "암호화 키 재생성 경고",
                "기존의 암호화키는 더 이상 사용될 수 없으며, 기존 암호화키를 사용해서 암호화된 데이터를 읽어들일 수 없게 됩니다.\n정말 새로운 암호화 키를 생성하시겠습니까?",
                "YES",
                "NO"
            );
            if( !confirmed )
            {
                Debug.Log( "[CryptoKeySettingsEditorUtility] Crypto key generation canceled." );
                return false;
            }

            var settings = AssetDatabase.LoadAssetAtPath<CryptoKeySetting>( CryptoKeyAssetPath );
            if( settings == null )
            {
                Debug.LogError( $"[CryptoKeySettingsEditorUtility] CryptoKeySettings asset not found. Path: {CryptoKeyAssetPath}" );
                return false;
            }
            SerializedObject serializedObject = new SerializedObject(settings);
            SerializedProperty keyProperty = serializedObject.FindProperty(KeyPropertyName);

            if( keyProperty == null )
            {
                Debug.LogError( $"[CryptoKeySettingsEditorUtility] Serialized property not found: {KeyPropertyName}" );
                return false;
            }

            Undo.RecordObject( settings, "Generate Crypto Key" );

            var newKey = CryptoKeyGenerator.GenerateDateTimeBased16ByteKey();
            keyProperty.stringValue = newKey;
            serializedObject.ApplyModifiedProperties();

            EditorUtility.SetDirty( settings );
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log( $"[CryptoKeySettingsEditorUtility] Crypto key generated and saved. Path: {CryptoKeyAssetPath}, Key: {MaskKey( newKey )}" );
            return true;
        }

        private static string MaskKey( string key )
        {
            if( string.IsNullOrEmpty( key ) )
            {
                return "<empty>";
            }

            if( key.Length <= 8 )
            {
                return "********";
            }
            return $"{key.Substring( 0, 4 )}...{key.Substring( key.Length - 4 )}";
        }
    }
}
