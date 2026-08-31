using System;
using System.IO;

namespace StudioSystemSDK.Domain
{
    public class SystemPathValue
    {
        private const string Config = "Config";
        public static string ConfigOriginRoot => Path.Combine( UnityEngine.Application.streamingAssetsPath, Config );
        public static string ConfigDestinationRoot => Path.Combine( UnityEngine.Application.persistentDataPath, Config );
    }
}