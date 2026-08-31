namespace StudioSystemSDK.Domain
{
    /// <summary>
    /// 암호화/복호화를 처리하는 Interface
    /// </summary>
    public interface ICryptoProcessDomain
    {
        /// <summary>
        /// 평문을 암호화 후 암호문을 반환
        /// </summary>
        /// <param name="rawData">원본 평문</param>
        /// <param name="key">암호화 Key</param>
        /// <returns>암호문</returns>
        string ConvertEncryptedString( string rawData, string key );
        /// <summary>
        /// 평문을 암호화 후 암호화 된 Byte 배열을 반환
        /// </summary>
        /// <param name="rawData">원본 평문</param>
        /// <param name="key">암호화 Key</param>
        /// <returns>암호화 된 Byte 배열</returns>
        byte[] ConvertEncryptedBytes( string rawData, string key );

        /// <summary>
        /// 암호문을 복호화 후 평문을 반환
        /// </summary>
        /// <param name="rawData">원본 암호문</param>
        /// <param name="key">복호화 Key</param>
        /// <returns>평문</returns>
        string ConvertDecryptedString( string encryptedData, string key );

        /// <summary>
        /// 암호문을 복호화 후 평문 Byte 배열을 반환
        /// </summary>
        /// <param name="rawData">원본 암호문</param>
        /// <param name="key">복호화 Key</param>
        /// <returns>평문 Byte 배열</returns>
        byte[] ConvertDecryptedBytes( string encryptedData, string key );
    }
}
