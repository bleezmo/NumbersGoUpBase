using Newtonsoft.Json;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.JsonModels
{
    public class TradierProfileWrapper
    {
        [JsonProperty("profile")]
        public TradierProfile Profile { get; set; }
    }
    public class TradierProfile
    {
        [JsonProperty("account")]
        [JsonConverter(typeof(SafeCollectionConverter))]
        public TradierAccount[] Accounts { get; set; }
    }
    public class TradierAccount
    {
        [JsonProperty("account_number")]
        public string AccountNumber { get; set; }
        [JsonProperty("classification")]
        public string Classification { get; set; }
    }
}
