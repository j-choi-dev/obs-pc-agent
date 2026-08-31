using Cysharp.Threading.Tasks;

namespace StudioSystemSDK.Domain
{
    /// <summary>
    /// 파일 처리 관련 Interface
    /// </summary>
    public interface IFileSystemDomain
    {
        /// <summary>
        /// 디렉토리 존재 여부 확인
        /// </summary>
        /// <param name="path">폴더명</param>
        /// <returns>디렉토리 존재 여부</returns>
        bool IsDirectoryExist( string path );
        /// <summary>
        /// 파일 존재 여부 확인
        /// </summary>
        /// <param name="fileName">파일명</param>
        /// <returns>파일 존재 여부</returns>
        bool IsFileExist( string fileName );
        /// <summary>
        /// 폴더명을 기준으로 디렉토리 생성
        /// </summary>
        /// <param name="path">폴더명</param>
        void CreateDirectory(string path );
        /// <summary>
        /// 공백 파일 생성
        /// </summary>
        /// <param name="fileName">파일명</param>
        /// <returns></returns>
        bool CreateFile( string fileName );
        /// <summary>
        /// 파일 경로를 기준으로 BIanry 파일 생성
        /// </summary>
        /// <param name="filePath">파일명</param>
        /// <param name="message">Binary로 저장할 문자열 데이터</param>
        /// <returns>저장 성공/실패</returns>
        UniTask<bool> SaveBinaryFile( string filePath, byte[] message );
        /// <summary>
        /// 파일 경로를 기준으로 텍스트 베이스 파일 생성
        /// </summary>
        /// <param name="filePath">파일명</param>
        /// <param name="message">Binary로 저장할 문자열 데이터</param>
        /// <returns>저장 성공/실패</returns>
        UniTask<bool> SaveTextFile( string filePath, string message );
        /// <summary>
        /// 파일 경로를 기준으로 Binary 파일 로드
        /// </summary>
        /// <param name="filePath">파일명/파일 경로</param>
        /// <returns>binary 데이터</returns>
        UniTask<byte[]> LoadBinaryFile( string filePath );
        /// <summary>
        /// 파일 경로를 기준으로 Binary 파일 로드
        /// </summary>
        /// <param name="filePath">파일명/파일 경로</param>
        /// <returns>binary 데이터의 문자열</returns>
        UniTask<string> LoadTextFile( string filePath );

        UniTask<bool> IsEqual( string originPath, string destPath );
        bool CopyFile( string originPath, string destPath, bool isOverWrite );
    }
}
