namespace LiveAppCore.Google.Domain
{
    /// <summary>
    /// Google Auth 정보를 로컬에 저장하는 처리 관련 구현체 Class
    /// </summary>
    public class GoogleAuthInfoStorage : IGoogleAuthInfoStorage
    {
        public GoogleOAuthSettings AuthSetting { get; private set; }

        public string Token { get; private set; }

        public void SetOAuthSettings( GoogleOAuthSettings setting )
        {
            AuthSetting = setting;
        }

        public void SetOAuthToken( string token )
        {
            Token = token;
        }
    }
}
