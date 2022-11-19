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
    public class TradierHistoryBars
    {
        [JsonProperty("history")]
        [JsonConverter(typeof(SafeNullConverter))]
        public History History { get; set; }
    }
    public class History
    {
        [JsonProperty("day")]
        [JsonConverter(typeof(SafeCollectionConverter))]
        public Bar[] Quotes { get; set; }
    }
    public class Bar
    {
        [JsonProperty("date")]
        public string Date { get; set; }
        [JsonProperty("open")]
        public double Open { get; set; }
        [JsonProperty("close")]
        public double Close { get; set; }
        [JsonProperty("high")]
        public double High { get; set; }
        [JsonProperty("low")]
        public double Low { get; set; }
        [JsonProperty("volume")]
        public long Volume { get; set; }
        [JsonIgnore]
        public DateTime BarDay => DateTime.Parse(Date, null, DateTimeStyles.AssumeLocal);
    }
}
