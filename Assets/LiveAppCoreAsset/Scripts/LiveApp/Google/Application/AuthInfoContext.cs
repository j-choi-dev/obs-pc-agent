using Cysharp.Threading.Tasks;
using LiveAppCore.Google.Domain;
using StudioSystemSDK.Domain;
using System;
using System.IO;
using System.Linq;
using System.Text;
using UniRx;
using UnityEngine;

namespace LiveAppCore.Google.Application
{
    /// <summary>
    /// Auth 정보 처리 관련 Application층 구현 클래스
    /// </summary>
    public class AuthInfoContext : IAuthInfoContext
    {
        private IFileSystemDomain _fileSystemDomain;
        private IFileSerializeDomain _fileSerializeDomain;
        private IGoogleAuthInfoStorage _googleAuthInfoStorage;
        private IGoogleAuthTokenDomain _googleAuthDomain;
        private ICryptoKeySettingDomain _cryptoKeySetting;
        private ICryptoProcessDomain _cryptoDomain;

        public AuthInfoContext( IFileSystemDomain fileSystemDomain,
            IFileSerializeDomain fileSerializeDomain,
            IGoogleAuthInfoStorage googleAuthInfoStorage,
            IGoogleAuthTokenDomain googleAuthDomain,
            ICryptoKeySettingDomain cryptoKeySettingDomain,
            ICryptoProcessDomain cryptoDomain )
        {
            _fileSystemDomain = fileSystemDomain;
            _fileSerializeDomain = fileSerializeDomain;
            _googleAuthInfoStorage = googleAuthInfoStorage;
            _googleAuthDomain = googleAuthDomain;
            _cryptoKeySetting = cryptoKeySettingDomain;
            _cryptoDomain = cryptoDomain;
        }

        private Subject<bool> _onCompleteTokenProcess = new Subject<bool>();
        public IObservable<bool> OnCompleteTokenProcess => _onCompleteTokenProcess;

        public string Token => _googleAuthDomain.Token;

        public async UniTask<bool> InitilizeAuthProcess()
        {
            try
            {
                var originPath = Path.Combine(SystemPathValue.ConfigOriginRoot, OAuthConstValue.BinFileName);
                var destPath = Path.Combine(SystemPathValue.ConfigDestinationRoot, OAuthConstValue.BinFileName);
                if( _fileSystemDomain.IsFileExist( originPath ) == false )
                {
                    throw new FileNotFoundException( "File Not Exist :: ", originPath );
                }

                var isOriginExists = _fileSystemDomain.IsFileExist(originPath);
                if( isOriginExists == false )
                {
                    throw new FileNotFoundException( "Google OAuth auth.bin does not exist.", originPath );
                }

                var isDestExists = _fileSystemDomain.IsFileExist(destPath);
                if( isDestExists == false )
                {
                    _fileSystemDomain.CopyFile( originPath, destPath, true );
                }
                var sourceBytes = await _fileSystemDomain.LoadBinaryFile( originPath );
                var destBytes = await _fileSystemDomain.LoadBinaryFile( destPath );
                var isEqulaFile = sourceBytes.SequenceEqual( destBytes );
                if( isEqulaFile == false )
                {
                    _fileSystemDomain.CopyFile( originPath, destPath, true );
                }

                var rawData = Encoding.Default.GetString(destBytes);
                var decryptedText = _cryptoDomain.ConvertDecryptedString( rawData, _cryptoKeySetting.CryptoKey );
                var oauthSettings = _fileSerializeDomain.DeserializeFromJson<GoogleOAuthSettings>( decryptedText );

                _googleAuthInfoStorage.SetOAuthSettings( oauthSettings );
                _googleAuthDomain.SetAuthValue( oauthSettings );
                var token = await _googleAuthDomain.GetAccessTokenAsync();
                _googleAuthInfoStorage.SetOAuthToken( token );
                _onCompleteTokenProcess.OnNext( true );
                return true;
            }
            catch( Exception e )
            {
                Debug.LogError( $"Auth failed. {e.GetType().Name}: {e.Message}" );
                _onCompleteTokenProcess.OnNext( false );
                return false;
            }
        }
    }
}
