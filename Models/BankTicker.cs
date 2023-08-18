using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Models
{
    [Index(nameof(Symbol), IsUnique = true)]
    [Index(nameof(PerformanceVector))]
    [Index(nameof(LastCalculatedPerformanceMillis))]
    [Index(nameof(LastCalculatedFinancialsMillis))]
    public class BankTicker : ITicker
    {
        public long Id { get; set; }
        public string Symbol { get; set; }
        public string Sector { get; set; }
        public double MarketCap { get; set; }
        public double Earnings { get; set; }
        public double EPS { get; set; }
        public double PERatio { get; set; }
        public double EVEarnings { get; set; }
        public double CurrentRatio { get; set; }
        public double DebtEquityRatio { get; set; }
        public double DividendYield { get; set; }
        public double DebtMinusCash { get; set; }
        public double Shares { get; set; }
        public double PriceChangeAvg { get; set; }
        public double BetaAvg { get; set; }
        public double PerformanceVector { get; set; }
        public string Country { get; set; }
        public double CurrentPriceDiff { get; set; }
        public DateTime? LastCalculatedPerformance { get; set; }
        public long? LastCalculatedPerformanceMillis { get; set; }
        public DateTime? LastCalculatedFinancials { get; set; }
        public long? LastCalculatedFinancialsMillis { get; set; }
    }
}
