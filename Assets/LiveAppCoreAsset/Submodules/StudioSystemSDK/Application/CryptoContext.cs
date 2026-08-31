using Cysharp.Threading.Tasks;
using StudioSystemSDK.Domain;

namespace StudioSystemSDK.Application
{
    /// <summary>
    /// 암호화/복호화된 데이터 취득을 위한 Application 구현체
    /// </summary>
    public class CryptoContext : ICryptoContext
    {
        private ICryptoProcessDomain _cryptoDomain;

        public CryptoContext( ICryptoProcessDomain cryptoDomain )
        {
            _cryptoDomain = cryptoDomain;
        }

        public async UniTask<string> ConvertToDecryptedData( string rawData, string key )
        {
            var result = _cryptoDomain.ConvertDecryptedString( rawData, key );
            return result;
        }

        public async UniTask<string> ConvertToEncryptedData( string rawData, string key )
        {
            var result = _cryptoDomain.ConvertEncryptedString( rawData, key );
            return result;
        }
    }
}
