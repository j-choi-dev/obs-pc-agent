using Cysharp.Threading.Tasks;

namespace StudioSystemSDK.Application
{
    /// <summary>
    /// 파일 처리 프로세스 관련 Application
    /// </summary>
    public interface IFileSystemContext
    {
        /// <summary>
        /// Binary 파일 취득
        /// </summary>
        /// <param name="path">파일 경로/파일명</param>
        /// <returns>binary파일의 문자열 데이터</returns>
        UniTask<string> ReadBinaryFile( string path );
        /// <summary>
        /// Binary 파일 보존
        /// </summary>
        /// <param name="path">파일 경로/파일명</param>
        /// <param name="message">보존할 데이터</param>
        /// <returns>파일 보존 처리 결과</returns>
        UniTask<bool> SaveBinaryFile( string path, string message );
    }
}
