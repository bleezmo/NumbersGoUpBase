using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUpBase.Utils
{
    public static class SchwabConfig
    {
        public const string BaseUrl = "https://api.schwabapi.com";
        public const string TokenRefreshEndpoint = "/v1/oauth/token";
        public const string PriceHistoryEndpoint = "/marketdata/v1/pricehistory?symbol={0}&periodType=year&period=5&frequencyType=daily&startDate={1}";
        public const string QuoteEndpoint = "/marketdata/v1/quotes?symbols={0}&fields=quote&indicative=false";
        public const string AccountsEndpoint = "/trader/v1/accounts/accountNumbers";
        public const string AccountDetailsEndpoint = "/trader/v1/accounts/{0}?fields=positions";
        public const string MarketHoursEndpoint = "/marketdata/v1/markets/equity?date={0}";
        public const string TransactionHistoryTradeEndpoint = "/trader/v1/accounts/{0}/transactions?startDate={1}&endDate={2}&types=TRADE&types=DIVIDEND_OR_INTEREST";
        public const string FinancialsEndpoint = "/marketdata/v1/instruments?symbol={0}&projection=fundamental";
        public const string OrderEndpoint = "/trader/v1/accounts/{0}/orders";
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
