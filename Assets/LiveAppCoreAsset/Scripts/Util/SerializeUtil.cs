using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;

namespace LiveApp.Util
{
    public class SerializeUtil
    {
        public static byte[] Serialize( object obj )
        {
            return System.Text.Encoding.UTF8.GetBytes( JsonUtility.ToJson( obj ) );
        }

        public static T Deserialize<T>( byte[] message )
        {
            return ( T )JsonUtility.FromJson<T>( System.Text.Encoding.UTF8.GetString( message ) );
        }

        public static byte[] Object2Byte( object[] obj )
        {
            var bf = new BinaryFormatter();
            using( var ms = new MemoryStream() )
            {
                try
                {
                    bf.Serialize( ms, obj );
                }
                catch( NullReferenceException e )
                {
                    Debug.Log( e.Message );
                }
                return ms.ToArray();
            }
        }

        public static T Byte2Object<T>( byte[] message )
        {
            var bf = new BinaryFormatter();
            using( var ms = new MemoryStream( message ) )
            {
                return ( T )bf.Deserialize( ms );
            }
        }

        public static string ToJson<T>( T obj )
        {
            var wapper=new JsonWrapperClass<T>();
            wapper.value = obj;
            var json = JsonUtility.ToJson(wapper);
            return json;
        }
        public static T FromJson<T>( string json )
        {
            var value = JsonUtility.FromJson<JsonWrapperClass<T>>(json);
            return value.value;
        }
        [System.Serializable]
        public class JsonWrapperClass<T>
        {
            public T value;
        }
    }
}
