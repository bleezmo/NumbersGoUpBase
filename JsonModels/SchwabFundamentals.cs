using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUpBase.JsonModels
{
    public class SchwabFundamentals
    {
        public SchwabFundamentalsInstrument[] Instruments { get; set; }
    }
    public class SchwabFundamentalsInstrument
    {
        public string Symbol { get; set; }
        public string AssetType { get; set; }
        public string Description { get; set; }
        public string Exchange { get; set; }
        public string Cusip { get; set; }
        public SchwabFundamental Fundamental { get; set; }
    }
    public class SchwabFundamental
    {
        public string Symbol { get; set; }
        public double DividendYield { get; set; }
        public double PeRatio { get; set; } //price-earnings
        public double PegRatio { get; set; } //price-earnings-growth
        public double PbRatio { get; set; } //price-book
        public double PcfRatio { get; set; } //price-cashflow
        public double ReturnOnEquity { get; set; }
        public double ReturnOnAssets { get; set; }
        public double ReturnOnInvestment { get; set; }
        public double QuickRatio { get; set; }
        public double CurrentRatio { get; set; }
        public double TotalDebtToCapital { get; set; }
        public double TotalDebtToEquity { get; set; }
        public double EpsTTM { get; set; }
        public double EpsChangePercentTTM { get; set; }
        public double RevChangeTTM { get; set; }
        public double SharesOutstanding { get; set; }
        public double MarketCap { get; set; }
    }
}
