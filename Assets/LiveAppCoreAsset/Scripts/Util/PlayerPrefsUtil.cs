using UnityEngine;
namespace LiveApp.Util
{
    public static class PlayerPrefsUtil
    {
        public static bool IsExistKey( string key )
            => PlayerPrefs.HasKey( key );

        public static string GetStringValueByKey(string key)
        {
            return PlayerPrefs.HasKey( key ) ?
                PlayerPrefs.GetString( key, string.Empty ) :
                string.Empty;
        }

        public static void SetStringValueByKey( string key, string value )
            => PlayerPrefs.SetString( key, value );
    }
}