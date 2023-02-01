using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Utils
{
    public class TradierPostOrder
    {
        private const string ORDERCLASS = "class";
        private const string ORDERCLASS_EQUITY = "equity";

        private const string SYMBOL = "symbol";

        private const string ORDERSIDE = "side";
        private const string ORDERSIDE_BUY = "buy";
        private const string ORDERSIDE_SELL = "sell";

        private const string QUANTITY = "quantity";

        private const string ORDERTYPE = "type";
        private const string ORDERTYPE_MARKET = "market";
        private const string ORDERTYPE_LIMIT = "limit";

        private const string DURATION = "duration";
        private const string DURATION_DAY = "day";
        private const string DURATION_GTC = "gtc";

        private const string PRICE = "price";
        private const string STOP = "stop";
        private const string TAG = "tag";
        private
        Dictionary<string, string> _orderParams = new Dictionary<string, string>
        {
            { ORDERCLASS, ORDERCLASS_EQUITY }
        };
        public string OrderClass { get => _orderParams[ORDERCLASS]; set => _orderParams[ORDERCLASS] = value; }
        public string Symbol { get => _orderParams[SYMBOL]; set => _orderParams[SYMBOL] = value; }
        public string OrderSide { get => _orderParams[ORDERSIDE]; set => _orderParams[ORDERSIDE] = value; }
        public int Quantity { get => int.Parse(_orderParams[QUANTITY]); set => _orderParams[QUANTITY] = value.ToString(); }
        public string OrderType { get => _orderParams[ORDERTYPE]; set => _orderParams[ORDERTYPE] = value; }
        public string Duration { get => _orderParams[DURATION]; set => _orderParams[DURATION] = value; }
        public double Price { get => Convert.ToDouble(_orderParams[PRICE]); set => _orderParams[PRICE] = Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00"); }
        public double Stop { get => Convert.ToDouble(_orderParams[STOP]); set => _orderParams[STOP] = Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00"); }
        public string Tag { get => _orderParams[TAG]; set => _orderParams[TAG] = value; }

        public override string ToString() => string.Join('&', _orderParams.Select(kv => $"{kv.Key}={kv.Value}"));
        public HttpContent HttpContent() => new FormUrlEncodedContent(_orderParams);

        public static TradierPostOrder Buy(string symbol, int quantity, double? limit = null)
        {
            var order = BaseOrder(symbol, quantity);
            order.OrderSide = ORDERSIDE_BUY;
            order.OrderType = limit.HasValue ? ORDERTYPE_LIMIT : ORDERTYPE_MARKET;
            if (limit.HasValue) { order.Price = limit.Value; }
            return order;
        }
        public static TradierPostOrder Sell(string symbol, int quantity, double? limit = null)
        {
            var order = BaseOrder(symbol, quantity);
            order.OrderSide = ORDERSIDE_SELL;
            order.OrderType = limit.HasValue ? ORDERTYPE_LIMIT : ORDERTYPE_MARKET;
            if (limit.HasValue) { order.Price = limit.Value; }
            return order;
        }
        private static TradierPostOrder BaseOrder(string symbol, int quantity)
        {
            return new TradierPostOrder
            {
                Symbol = symbol,
                Quantity = quantity,
                Duration = DURATION_DAY,
                Tag = Guid.NewGuid().ToString()
            };
        }
    }
}
