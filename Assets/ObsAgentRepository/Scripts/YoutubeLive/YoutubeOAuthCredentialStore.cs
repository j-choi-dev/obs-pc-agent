using System;
using System.IO;
using UnityEngine;

namespace ObsAgent
{
    public sealed class YoutubeOAuthCredentialStore
    {
        private const string FileName = "youtube-oauth.json";

        [Serializable]
        private sealed class Data
        {
            public string refreshToken;
        }

        public string FilePath { get; private set; }

        public YoutubeOAuthCredentialStore(string persistentDataPath )
        {
            if( string.IsNullOrWhiteSpace( persistentDataPath ) )
            {
                throw new ArgumentException( "Persistent Data Path가 비어 있습니다.", nameof( persistentDataPath ) );
            }
            FilePath = Path.Combine( persistentDataPath, FileName );
            string directory = Path.GetDirectoryName( FilePath );

            if( !string.IsNullOrWhiteSpace( directory ) )
            {
                Directory.CreateDirectory( directory );
            }
        }

        public string LoadRefreshToken()
        {
            try
            {
                if( !File.Exists( FilePath ) )
                {
                    return string.Empty;
                }
                string json = File.ReadAllText( FilePath );
                Data data = JsonUtility.FromJson<Data>( json );
                return data?.refreshToken ?? string.Empty;
            }
            catch( Exception exception )
            {
                Debug.LogException( exception );
                return string.Empty;
            }
        }

        public void SaveRefreshToken( string refreshToken )
        {
            if( string.IsNullOrWhiteSpace( refreshToken ) )
            {
                throw new ArgumentException( "Refresh Token이 비어 있습니다." );
            }
            var data = new Data
            {
                refreshToken = refreshToken
            };
            string json = JsonUtility.ToJson( data, true );
            File.WriteAllText( FilePath, json );
        }
    }
}