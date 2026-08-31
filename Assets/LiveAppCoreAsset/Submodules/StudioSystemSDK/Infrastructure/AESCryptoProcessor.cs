using System;
using System.Security.Cryptography;
using System.Text;

namespace StudioSystemSDK.Domain
{
    public class AESCryptoProcessor : ICryptoProcessDomain
    {
        //private string encryptionKey = "1234567890123456"; // 16자리 키

        public string ConvertEncryptedString( string rawData, string key )
            => Convert.ToBase64String( ConvertEncryptedBytes( rawData, key ) );

        public byte[] ConvertEncryptedBytes( string rawData, string key )
        {
            var encryptedData = string.Empty;
            using( Aes aes = Aes.Create() )
            {
                aes.Key = Encoding.UTF8.GetBytes( key );
                aes.IV = new byte[16]; // IV는 0으로 초기화하여 사용
                ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                byte[] textBytes = Encoding.UTF8.GetBytes(rawData);

                byte[] encryptedBytes = encryptor.TransformFinalBlock(textBytes, 0, textBytes.Length);
                encryptedData = Convert.ToBase64String( encryptedBytes );
                return encryptedBytes;
            }
        }

        public string ConvertDecryptedString( string encryptedData, string key )
            => Encoding.UTF8.GetString( ConvertDecryptedBytes( encryptedData, key ) );

        public byte[] ConvertDecryptedBytes( string encryptedData, string key )
        {
            string decryptedData = string.Empty;
            using( Aes aes = Aes.Create() )
            {
                aes.Key = Encoding.UTF8.GetBytes( key );
                aes.IV = new byte[16]; // IV는 암호화할 때와 동일하게 설정
                ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                byte[] encryptedBytes = Convert.FromBase64String(encryptedData);
                byte[] decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
                decryptedData = Encoding.UTF8.GetString( decryptedBytes );
                return decryptedBytes;
            }
        }
    }
}
