namespace LiveAppCore.Google.Domain
{
    /// <summary>
    /// Google Auth 정보를 로컬에 저장하는 처리 관련 Interface
    /// </summary>
    public interface IGoogleAuthInfoStorage
    {
        /// <summary>
        /// Auth Setting 데이터
        /// </summary>
        GoogleOAuthSettings AuthSetting { get; }
        /// <summary>
        /// 토큰
        /// </summary>
        string Token { get; }

        /// <summary>
        /// Auth Setting 데이터 저장
        /// </summary>
        /// <param name="setting">Auth Setting Data Class</param>
        void SetOAuthSettings( GoogleOAuthSettings setting );
        /// <summary>
        /// Token 정보 저장
        /// </summary>
        /// <param name="token">Token 값</param>
        void SetOAuthToken( string token );
    }
}
