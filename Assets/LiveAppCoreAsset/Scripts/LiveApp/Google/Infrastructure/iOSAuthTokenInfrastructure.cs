using Cysharp.Threading.Tasks;
using LiveAppCore.Google.Domain;
using System;
using System.Threading;
using UnityEngine;

namespace LiveAppCore.Google.Infrastructure
{
    /// <summary>
    /// GoogleOAuthToken 취득을 위한 구현 클래스
    /// </summary>
    public class iOSAuthTokenInfrastructure : IGoogleAuthTokenDomain
    {
        private string _clientId;
        private string _scope;
        private string _playerPrefsKey;

        private GoogleOAuthToken _cachedToken;
        private INativeSigninDomain _signInDomain;

        public string Token => _cachedToken?.accessToken;

        public iOSAuthTokenInfrastructure( INativeSigninDomain signInDomain)
        {
            _signInDomain = signInDomain;
        }

        public void SetAuthValue( GoogleOAuthSettings settings )
        {
            _clientId = settings.IOSClientId;
            _scope  = settings.SheetsReadonlyScope;
            _playerPrefsKey = $"GOOGLE_OAUTH_TOKEN_{_clientId}_{_scope}".GetHashCode().ToString();
        }

        public async UniTask<string> GetAccessTokenAsync( CancellationToken cancellationToken = default )
        {
            if( string.IsNullOrWhiteSpace( _clientId ) )
            {
                throw new InvalidOperationException( "Google OAuth iOS Client ID is empty." );
            }

            if( string.IsNullOrWhiteSpace( _scope ) )
            {
                throw new InvalidOperationException( "Google OAuth scope is empty." );
            }

            _cachedToken = await _signInDomain.RequestAccessTokenAsync( _clientId, _scope, cancellationToken );

            if( _cachedToken == null )
            {
                throw new Exception( "GoogleOAuthToken is null." );
            }

            if( string.IsNullOrWhiteSpace( _cachedToken.accessToken ) )
            { 
                throw new Exception( "Google access token is empty." );
            }

            return _cachedToken.accessToken;
        }

        public void ClearAllPrefs()
        {
            PlayerPrefs.DeleteAll();
        }
    }
}
