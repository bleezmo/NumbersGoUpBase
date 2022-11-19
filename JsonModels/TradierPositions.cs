using Newtonsoft.Json;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.JsonModels
{
    public class TradierPositionsWrapper
    {
        [JsonProperty("positions")]
        [JsonConverter(typeof(SafeNullConverter))]
        public TradierPositions TradierPositions { get; set; }
    }
    public class TradierPositions
    {
        [JsonProperty("position")]
        [JsonConverter(typeof(SafeCollectionConverter))]
        public TradierPosition[] Positions { get; set; }
    }
    public class TradierPosition
    {
        [JsonProperty("cost_basis")]
        public double CostBasis { get; set; }
        [JsonProperty("date_acquired")]
        public string DateAcquiredStr { get; set; }
        [JsonProperty("id")]
        public long Id { get; set; }
        [JsonProperty("quantity")]
        public double Quantity { get; set; }
        [JsonProperty("symbol")]
        public string Symbol { get; set; }
    }
}
