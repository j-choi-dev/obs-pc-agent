using System;
using System.Security.Cryptography;
using System.Text;

namespace LiveAppCore.Editor
{
    public static class CryptoKeyGenerator
    {
        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        public static string GenerateDateTimeBased16ByteKey()
        {
            // 오늘 날짜/시간 값을 seed 성격으로 섞음
            string timeText = DateTime.Now.ToString("yyyyMMddHHmmssfffffff");

            // 추가 난수 섞기
            byte[] randomBytes = new byte[32];
            using( RandomNumberGenerator rng = RandomNumberGenerator.Create() )
            {
                rng.GetBytes( randomBytes );
            }

            string source = timeText + Convert.ToBase64String(randomBytes);

            byte[] hashBytes;
            using( SHA256 sha256 = SHA256.Create() )
            {
                hashBytes = sha256.ComputeHash( Encoding.UTF8.GetBytes( source ) );
            }

            char[] result = new char[16];

            for( int i = 0; i < result.Length; i++ )
            {
                result[i] = chars[hashBytes[i] % chars.Length];
            }

            return new string( result );
        }
    }
}