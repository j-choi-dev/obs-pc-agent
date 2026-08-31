using Cysharp.Threading.Tasks;
using System.Threading;
using UnityEngine;

namespace LiveAppCore.Google.Domain
{
    /// <summary>
    /// 구글 인증 토큰을 취득하기 위한 Interface
    /// </summary>
    public interface IGoogleAuthTokenDomain
    {
        /// <summary>
        /// 토큰값
        /// </summary>
        string Token { get; }

        /// <summary>
        /// 구글 인증 정보 세팅
        /// </summary>
        /// <param name="settings">구글 OAuth 인증 데이터 클래스</param>
        void SetAuthValue( GoogleOAuthSettings settings );
        
        /// <summary>
        /// 토큰 취득 프로세스
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>토큰 값</returns>
        UniTask<string> GetAccessTokenAsync( CancellationToken cancellationToken = default );
        void ClearAllPrefs();
    }
}