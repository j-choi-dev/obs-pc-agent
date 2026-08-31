namespace StudioSystemSDK.Domain
{
    /// <summary>
    /// 암호화/복호화 키 취득을 위한 Interface
    /// </summary>
    public interface ICryptoKeySettingDomain
    {
        /// <summary>
        /// 암호/복호화 키
        /// </summary>
        string CryptoKey { get; }
    }
}
