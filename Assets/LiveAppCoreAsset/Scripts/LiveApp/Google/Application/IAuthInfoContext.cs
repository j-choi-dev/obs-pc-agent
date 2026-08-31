using Cysharp.Threading.Tasks;
using System;

namespace LiveAppCore.Google.Application
{
    /// <summary>
    /// 구글 인증을 위한 절차를 정의한 Application
    /// </summary>
    public interface IAuthInfoContext
    {
        /// <summary>
        /// 토큰값
        /// </summary>
        string Token { get; }

        /// <summary>
        /// 토큰 취득 완료 이벤트
        /// </summary>
        IObservable<bool> OnCompleteTokenProcess { get; }

        /// <summary>
        /// 인증을 위한 초기화 작업 수행
        /// </summary>
        /// <returns>인증을 위한 초기화 작업</returns>
        UniTask<bool> InitilizeAuthProcess();
    }
}
