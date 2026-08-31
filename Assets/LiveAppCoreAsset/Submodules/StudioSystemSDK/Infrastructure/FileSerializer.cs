using MiniJSON;
using SimpleJSON;
using StudioSystemSDK.Domain;
using UnityEngine;

namespace StudioSystemSDK.Infrastructure
{
    public class FileSerializer : IFileSerializeDomain
    {
        public T DeserializeFromJson<T>( string rawMessage )
        {
            if( string.IsNullOrWhiteSpace( rawMessage ) )
            {
                Debug.LogError( "JSON string is null or empty." );
                return default;
            }
            try
            {
                return JsonUtility.FromJson<T>( rawMessage );
            }
            catch( System.Exception e )
            {
                Debug.LogError( $"JSON parse failed: {e.Message}\nJSON: {rawMessage}" );
                return default;
            }

        }

        public string SerializeToBinary( string rawMessage )
        {
            throw new System.NotImplementedException();
        }
    }
}
