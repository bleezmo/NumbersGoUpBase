using Newtonsoft.Json;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.JsonModels
{
    public class TradierAccountHistory
    {
        [JsonProperty("history")]
        [JsonConverter(typeof(SafeNullConverter))]
        public TradierAccountHistoryEvents History { get; set; }
    }
    public class TradierAccountHistoryEvents
    {
        [JsonProperty("event")]
        [JsonConverter(typeof(SafeCollectionConverter))]
        public TradierAccountHistoryEvent[] Events { get; set; }
    }
    public class TradierAccountHistoryEvent
    {
        public const string DividendType = "dividend";
        public const string TradeType = "trade";

        public const string ACHType = "ach";
        public const string OptionType = "option";
        public const string WireType = "wire";
        public const string FeeType = "fee";
        public const string TaxType = "tax";
        public const string JournalType = "journal";
        public const string CheckType = "check";
        public const string TransferType = "transfer";
        public const string AdjustmentType = "adjustment";
        public const string InterestType = "interest";

        [JsonProperty("date")]
        public string DateStr { get; set; }
        [JsonProperty("amount")]
        public double Amount { get; set; }
        [JsonProperty("type")]
        public string TypeStr { get; set; }
        [JsonProperty(DividendType)]
        public TradierAccountHistoryEventDetails Dividend { get; set; }
        [JsonProperty(TradeType)]
        public TradierAccountHistoryEventDetails Trade { get; set; }
        [JsonIgnore]
        public DateTime Date => DateTime.Parse(DateStr, null, DateTimeStyles.AssumeLocal);
        [JsonIgnore]
        public TradierAccountHistoryEventDetails Details => Dividend != null ? Dividend : (Trade != null ? Trade : null);

    }
    public class TradierAccountHistoryEventDetails
    {
        [JsonProperty("price")]
        public double Price { get; set; }
        [JsonProperty("quantity")]
        public double Quantity { get; set; }
        [JsonProperty("symbol")]
        public string Symbol { get; set; }
    }
}
