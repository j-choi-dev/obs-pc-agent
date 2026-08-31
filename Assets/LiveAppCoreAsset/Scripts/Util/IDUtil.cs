using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;

namespace LiveApp.Util
{
    public static class IDRules
    {
        public static readonly string Separator = "_";
        public static readonly string PathSeparator = "/";
        public static readonly string SystemPrefix = "$";
    }

    /// <summary>
    /// App내에서 사용되는 ID에 대한 유틸리티 클래스
    /// </summary>
    public static class IDUtil
    {
        public static string GenerateNewId<T>( string idKey, IEnumerable<T> dataList, Func<T, string> IdSelector )
        {
            var keyDuplicates = dataList
                .Select( data => IdSelector( data ) )
                .Where( id => id.StartsWith( idKey ) )
                .ToList();
            if ( keyDuplicates.Count == 0 )
            {
                return idKey;
            }

            // 연번이 최대치인 요소를 추출하여, +1한 값을 반환
            int maxSerialNumber = 0;
            foreach ( var id in keyDuplicates )
            {
                var value = ExtractSerialNumber( id );
                if ( maxSerialNumber < value )
                {
                    maxSerialNumber = value;
                }
            }

            var newId = $"{idKey}{IDRules.Separator}{maxSerialNumber + 1}";
            Debug.Assert( dataList.All( data => IdSelector( data ) != newId ), $"newId({newId}) is already exists" );
            return newId;
        }

        public static string AttachIDSerialNumber( string id, string target )
        {
            var serialNumber = ExtractSerialNumber( id );
            if ( serialNumber != 0 )
            {
                return $"{target}{IDRules.Separator}{serialNumber}";
            }
            else
            {
                return target;
            }
        }

        private static int ExtractSerialNumber( string id )
        {
            var lastUnderScoreIndex = id.LastIndexOf( IDRules.Separator );
            if ( lastUnderScoreIndex < 0 )
            {
                return 0;
            }

            var serialNumberStr = id.Substring( lastUnderScoreIndex + 1 );
            if ( int.TryParse( serialNumberStr, out int value ) )
            {
                return value;
            }

            return 0;
        }
    }
}
