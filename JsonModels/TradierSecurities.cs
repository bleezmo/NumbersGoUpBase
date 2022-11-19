using Newtonsoft.Json;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.JsonModels
{
    public class TradierSecuritiesWrapper
    {
        [JsonProperty("securities")]
        [JsonConverter(typeof(SafeNullConverter))]
        public TradierSecurities TradierSecurities { get; set; }
    }
    public class TradierSecurities
    {
        [JsonProperty("security")]
        [JsonConverter(typeof(SafeCollectionConverter))]
        public TradierSecurity[] Securities { get; set; }
    }
    public class TradierSecurity
    {
        [JsonProperty("symbol")]
        public string Symbol { get; set; }
        [JsonProperty("type")]
        public string SecurityType { get; set; }
        [JsonProperty("description")]
        public string Description { get; set; }
    }
}
