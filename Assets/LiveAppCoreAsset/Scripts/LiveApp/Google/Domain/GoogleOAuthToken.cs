using System;

namespace LiveAppCore.Google.Domain
{
    /// <summary>
    /// GoogleOAuthToken 데이터 클래스
    /// </summary>
    public class GoogleOAuthToken
    {
        public string accessToken;
        public string refreshToken;
        public long expiresAtUnixTime;
        public string scope;
        public string tokenType;

        /// <summary>
        /// 유효한 토큰을 가지고 있는지 체크
        /// </summary>
        /// <returns>유효한 토큰을 가지고 있는지 결과값을 반환</returns>
        /// <remarks>만료 60초 전부터는 갱신 대상</remarks>
        public bool HasValidAccessToken()
        {
            if( string.IsNullOrEmpty( accessToken ) )
            {
                return false;
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return expiresAtUnixTime > now + OAuthConstValue.TimeSpanSecond;
        }
    }
}
