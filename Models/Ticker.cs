using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Models
{
    [Index(nameof(Symbol), IsUnique = true)]
    [Index(nameof(LastCalculatedMillis))]
    [Index(nameof(LastCalculatedPerformanceMillis))]
    [Index(nameof(LastCalculatedAvgsMillis))]
    [Index(nameof(PerformanceVector))]
    [Index(nameof(PERatio))]
    [Index(nameof(AvgMonthPerc))]
    public class Ticker : ITicker
    {
        public long Id { get; set; }
        public string Symbol { get; set; }
        public string Sector { get; set; }
        public double AvgMonthPerc { get; set; }
        public double MonthPercVariance { get; set; }
        public double MaxMonthConsecutiveLosses { get; set; }
        public double PerformanceVector { get; set; }
        public DateTime LastCalculated { get; set; }
        public long LastCalculatedMillis { get; set; }
        public DateTime? LastCalculatedPerformance { get; set; }
        public long? LastCalculatedPerformanceMillis { get; set; }
        public DateTime? LastCalculatedAvgs { get; set; }
        public long? LastCalculatedAvgsMillis { get; set; }
        public double EPS { get; set; }
        public double PERatio { get; set; }
        public double SMASMAAvg { get; set; }
        public double SMASMAStDev { get; set; }
        public double AlmaSma1Avg { get; set; }
        public double AlmaSma1StDev { get; set; }
        public double AlmaSma2Avg { get; set; }
        public double AlmaSma2StDev { get; set; }
        public double AlmaSma3Avg { get; set; }
        public double AlmaSma3StDev { get; set; }
        public double ProfitLossAvg { get; set; }
        public double ProfitLossStDev { get; set; }
        public double Earnings { get; set; }
        public double DividendYield { get; set; }
        public double AlmaVelStDev { get; set; }
        public double SMAVelStDev { get; set; }
        public double RegressionAngle { get; set; }
        public double EVEarnings { get; set; }

        public List<HistoryBar> HistoryBars { get; set; }
        public List<DbOrder> Orders { get; set; }
    }
}
