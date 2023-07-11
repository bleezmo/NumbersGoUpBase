using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.JsonModels
{
    public class TradierAccountBalance
    {
        [JsonProperty("balances")]
        public TradierBalance Balance { get; set; }
    }
    public class TradierBalance
    {
        [JsonProperty("total_equity")]
        public double Equity { get; set; }
        [JsonProperty("total_cash")]
        public double TotalCash { get; set; }
        public TradierCash Cash { get; set; }
        public TradierMargin Margin { get; set; }

    }
    public class TradierCash
    {
        [JsonProperty("cash_available")]
        public double CashAvailable { get; set; }
        [JsonProperty("unsettled_funds")]
        public double UnsettledFunds { get; set; }
    }
    public class TradierMargin
    {
        [JsonProperty("stock_buying_power")]
        public double BuyingPower { get; set; }
    }
}
