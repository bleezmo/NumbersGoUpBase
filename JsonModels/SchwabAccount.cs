using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUpBase.JsonModels
{
    public class SchwabAccount
    {
        public string AccountNumber {  get; set; }
        public string HashValue { get; set; }
    }
    public class SchwabAccountWrapper
    {
        public SchwabAccountDetails SecuritiesAccount { get; set; }
    }
    public class SchwabAccountDetails
    {
        public string AccountNumber { get; set; }
        public SchwabPosition[] Positions { get; set; }
        public SchwabAccountBalance CurrentBalances { get; set; }
    }

    public class SchwabPosition
    {
        public double? AveragePrice { get; set; }
        public double? LongQuantity { get; set; }
        public SchwabAccountInstrument Instrument { get; set; }
        public double? MarketValue { get; set; }

        public bool IsInvalid() => AveragePrice == null || LongQuantity == null;
    }

    public class SchwabAccountInstrument
    {
        public string Symbol { get; set; }
    }
    public class SchwabAccountBalance
    {
        public double CashBalance { get; set; }
        public double LiquidationValue { get; set; }
        public double LongMarketValue { get; set; }
        public double CashAvailableForTrading { get; set; }
    }
}
