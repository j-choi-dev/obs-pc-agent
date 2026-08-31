using StudioSystemSDK.Domain;
using UnityEngine;

namespace StudioSystemSDK.Infrastructure
{
    /// <summary>
    /// 암호화/복호화 키를 보관할 Scriptable Object 구현 클래스
    /// </summary>
    [CreateAssetMenu( fileName = "CryptoKeySetting", menuName = "LiveAppCore/Crypto/Crypto Key Settings" )]
    public class CryptoKeySetting : ScriptableObject, ICryptoKeySettingDomain
    {
        [SerializeField] private string _cyryptoKey;
        public string CryptoKey => _cyryptoKey;
    }
}
