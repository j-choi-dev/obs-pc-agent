using Cysharp.Threading.Tasks;
using StudioSystemSDK.Domain;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace StudioSystemSDK.Infrastructure
{
    /// <summary>
    /// 파일 처리 관련 구현체 클래스
    /// </summary>
    public class FileSystemInfrastructure : IFileSystemDomain
    {
        public FileSystemInfrastructure()
        {
        }
        
        public bool IsDirectoryExist( string path )
        {
            var dirPath = Path.GetDirectoryName( path );
            return Directory.Exists( dirPath );
        }

        public void CreateDirectory( string path )
        {
            var dirPath = Path.GetDirectoryName( path );
            if( Directory.Exists( dirPath ) )
            {
                return;
            }
            Directory.CreateDirectory( dirPath );
        }

        public bool IsFileExist( string filePath )
        {
            return File.Exists( filePath );
        }
        
        public async UniTask<string> LoadTextFile( string filePath )
        {
            var message = string.Empty;
            using( var fs = new FileStream( filePath, FileMode.Open, FileAccess.Read ) )
            using( var sr = new StreamReader( fs, false ) )
            {
                message = await sr.ReadToEndAsync();
            }
            return message;
        }

        public async UniTask<byte[]> LoadBinaryFile( string filePath )
        {
            var message = string.Empty;
            using( var fs = new FileStream( filePath, FileMode.Open, FileAccess.Read ) )
            using( var sr = new StreamReader( fs, false ) )
            {
                message = sr.ReadToEnd();
            }
            var bytes = Encoding.ASCII.GetBytes(message);
            return bytes;
        }

        public async UniTask<bool> SaveBinaryFile( string filePath, byte[] message )
        {
            using( var fs = new FileStream( filePath, FileMode.OpenOrCreate, FileAccess.Write ) )
            using( var sw = new StreamWriter( fs, System.Text.Encoding.UTF8 ) )
            {
                try
                {
                    sw.WriteLine( message );
                }
                catch( System.Exception e )
                {
                    Debug.LogError( e.Message );
                    return false;
                }
            }
            return true;
        }

        public async UniTask<bool> SaveTextFile( string filePath, string message )
        {
            using( var fs = new FileStream( filePath, FileMode.OpenOrCreate, FileAccess.Write ) )
            using( var sw = new StreamWriter( fs, System.Text.Encoding.UTF8 ) )
            {
                try
                {
                    sw.WriteLine( message );
                }
                catch( System.Exception e )
                {
                    Debug.LogError( e.Message );
                    return false;
                }
            }
            return true;
        }

        public bool CreateFile( string path )
        {
            try
            {
                using( FileStream fs = File.Create( path ) )
                {
                }
                return true;
            }
            catch( System.Exception e )
            {
                Debug.LogError( e.Message );
                return false;
            }
        }

        public bool CopyFile( string originPath, string destPath, bool isOverWrite )
        {
            try
            {
                var dir = Path.GetDirectoryName( destPath );
                if( Directory.Exists( dir ) == false )
                {
                    Directory.CreateDirectory( dir );
                }
                File.Copy(originPath, destPath, isOverWrite );
                UnityEngine.Debug.Log( $"FileSystem :: {originPath} -> {destPath}" );
                return true;
            }
            catch( System.Exception e )
            {
                Debug.LogError( e.Message );
                return false;
            }
        }

        public async UniTask<bool> IsEqual( string originPath, string destPath )
        {
            try
            {
                var sourceBytes = await LoadBinaryFile( originPath );
                var isDestExists = IsFileExist(destPath);
                if( isDestExists == false )
                {
                    return false;
                }
                var destBytes = await LoadBinaryFile( destPath );
                return sourceBytes.SequenceEqual( destBytes );
            }
            catch(System.Exception e)
            {
                Debug.LogError( e.Message );
                return false;
            }
        }
    }
}
