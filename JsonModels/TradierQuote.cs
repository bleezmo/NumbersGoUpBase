using Newtonsoft.Json;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.JsonModels
{
    public class TradierQuotesWrapper
    {
        [JsonProperty("quotes")]
        public TradierQuotes TradierQuotes { get; set; }
    }
    public class TradierQuotes
    {
        [JsonProperty("quote")]
        [JsonConverter(typeof(SafeCollectionConverter))]
        public TradierQuote[] Quotes { get; set; }
    }
    public class TradierQuote
    {
        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("last")]
        public double Price { get; set; }

        [JsonProperty("last_volume")]
        public long Size { get; set; }

        [JsonProperty("trade_date")]
        public long TradeTimeMilliseconds { get; set; }

        [JsonIgnore]
        public DateTime TradeTime => DateTimeOffset.FromUnixTimeMilliseconds(TradeTimeMilliseconds).LocalDateTime;
    }
}
