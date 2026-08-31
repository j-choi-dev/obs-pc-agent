using Cysharp.Threading.Tasks;

namespace StudioSystemSDK.Application
{
    /// <summary>
    /// 암호화/복호화된 데이터 취득을 위한 Application Interface
    /// </summary>
    public interface ICryptoContext
    {
        /// <summary>
        /// 텍스트 데이터를 암호화된 데이터로 변환
        /// </summary>
        /// <param name="rawData">원본 평문 데이터</param>
        /// <param name="key">암호화/복호화 키값</param>
        /// <returns>암호화된 텍스트 데이터</returns>
        UniTask<string> ConvertToEncryptedData( string rawData, string key );
        /// <summary>
        /// 암호화된 데이터를 복호화된 데이터로 변환 
        /// </summary>
        /// <param name="rawData">원본 암호문 데이터</param>
        /// <param name="key">암호화/복호화 키값</param>
        /// <returns>복호화된 텍스트 데이터</returns>
        UniTask<string> ConvertToDecryptedData( string rawData, string key );
    }
}
