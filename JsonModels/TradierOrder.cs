using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.JsonModels
{
    public class TradierOrdersWrapper
    {
        [JsonProperty("orders")]
        [JsonConverter(typeof(SafeNullConverter))]
        public TradierOrders TradierOrders { get; set; }
    }
    public class TradierOrderWrapper
    {
        [JsonProperty("order")]
        [JsonConverter(typeof(SafeNullConverter))]
        public TradierOrder Order { get; set; }
    }
    public class TradierOrders
    {
        [JsonProperty("order")]
        [JsonConverter(typeof(SafeCollectionConverter))]
        public TradierOrder[] Orders { get; set; }
    }
    public class TradierOrder
    {
        public const string LIMIT_ORDERTYPE = "limit";
        public const string MARKET_ORDERTYPE = "market";
        public const string STOP_ORDERTYPE = "stop";
        public const string STOP_LIMIT_ORDERTYPE = "stop_limit";

        public const string BUY_ORDERSIDE = "buy";
        public const string SELL_ORDERSIDE = "sell";

        public const string OPEN_ORDERSTATUS = "open";
        public const string PARTIALLYFILLED_ORDERSTATUS = "partially_filled";
        public const string FILLED_ORDERSTATUS = "filled";
        public const string EXPIRED_ORDERSTATUS = "expired";
        public const string CANCELED_ORDERSTATUS = "canceled";
        public const string PENDING_ORDERSTATUS = "pending";
        public const string REJECTED_ORDERSTATUS = "rejected";
        public const string ERROR_ORDERSTATUS = "error";

        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("symbol")]
        public string Symbol { get; set; }
        [JsonProperty("type")]
        public string OrderType { get; set; }
        [JsonProperty("side")]
        public string OrderSide { get; set; }
        [JsonProperty("exec_quantity")]
        public double Quantity { get; set; }
        [JsonProperty("status")]
        public string OrderStatus { get; set; }
        [JsonProperty("avg_fill_price")]
        public double AvgFillPrice { get; set; }
        [JsonProperty("tag")]
        public string Tag { get; set; }
        [JsonProperty("transaction_date")]
        public string TransactionDateStr { get; set; }
        [JsonIgnore]
        public DateTime TransactionDate => DateTime.Parse(TransactionDateStr);

    }
    public class TradierPostOrderResponseWrapper
    {
        [JsonProperty("order")]
        public TradierPostOrderResponse Order { get; set; }
    }
    public class TradierPostOrderResponse
    {
        public const string SUCCESS_ORDERSTATUS = "ok";

        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("status")]
        public string OrderStatus { get; set; }
    }
}
