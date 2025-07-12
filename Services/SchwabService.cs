using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUpBase.Services
{
    public class SchwabService
    {
        private readonly ISchwabAccessTokenService _accessTokenService;

        public SchwabService(ISchwabAccessTokenService accessTokenService) 
        { 
            _accessTokenService = accessTokenService;
        }
    }

    public interface ISchwabAccessTokenService
    {
        Task<string> GetAccessToken();
    }
    public class AuthTokens
    {
        public int expires_in { get; set; }
        public string? token_type { get; set; }
        public string? scope { get; set; }
        public string? refresh_token { get; set; }
        public string? access_token { get; set; }
        public string? id_token { get; set; }
    }
    public class DecryptProps
    {
        public byte[] Key { get; set; }
        public byte[] IV { get; set; }
        public byte[] EncryptedText { get; set; }
        public DecryptProps(byte[] encryptedText, byte[] key, byte[] iv)
        {
            Key = key;
            IV = iv;
            EncryptedText = encryptedText;
        }
    }
    public static class DecryptPropsExtension
    {
        public static string Decrypt(this DecryptProps props)
        {
            return DecryptStringFromBytes_Aes(props.EncryptedText, props.Key, props.IV);
        }
        private static string DecryptStringFromBytes_Aes(byte[] cipherText, byte[] Key, byte[] IV)
        {
            // Check arguments.
            if (cipherText == null || cipherText.Length <= 0)
                throw new ArgumentNullException("cipherText");
            if (Key == null || Key.Length <= 0)
                throw new ArgumentNullException("Key");
            if (IV == null || IV.Length <= 0)
                throw new ArgumentNullException("IV");

            // Declare the string used to hold
            // the decrypted text.
            string plaintext = null;

            // Create an Aes object
            // with the specified key and IV.
            using (Aes aesAlg = Aes.Create())
            {
                aesAlg.Key = Key;
                aesAlg.IV = IV;

                // Create a decryptor to perform the stream transform.
                ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

                // Create the streams used for decryption.
                using (MemoryStream msDecrypt = new MemoryStream(cipherText))
                {
                    using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                    {
                        using (StreamReader srDecrypt = new StreamReader(csDecrypt))
                        {

                            // Read the decrypted bytes from the decrypting stream
                            // and place them in a string.
                            plaintext = srDecrypt.ReadToEnd();
                        }
                    }
                }
            }

            return plaintext;
        }
    }
}
