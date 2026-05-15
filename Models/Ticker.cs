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
    public class Ticker : ITicker
    {
        public long Id { get; set; }
        public string Symbol { get; set; }
        public string Sector { get; set; }
        public double PerformanceVector { get; set; }
        public DateTime LastCalculated { get; set; }
        public long LastCalculatedMillis { get; set; }
        public DateTime? LastCalculatedPerformance { get; set; }
        public long? LastCalculatedPerformanceMillis { get; set; }
        public DateTime? LastCalculatedAvgs { get; set; }
        public long? LastCalculatedAvgsMillis { get; set; }
        public double SMASMAAvg { get; set; }
        public double SMASMAStDev { get; set; }
        public double SMA2SMAAvg { get; set; }
        public double SMA2SMAStDev { get; set; }
        public double AlmaSmaAvg { get; set; }
        public double AlmaSmaStDev { get; set; }
        public double ProfitLossAvg { get; set; }
        public double ProfitLossStDev { get; set; }
        public double Earnings { get; set; }
        public double DividendYield { get; set; }
        public double AlmaVelStDev { get; set; }
        public double SMAVelStDev { get; set; }
        public double WeekTrendAvg { get; set; }
        public double WeekTrendStDev { get; set; }
        public double WeekTrendVelStDev { get; set; }

        public List<HistoryBar> HistoryBars { get; set; }
        public List<DbOrder> Orders { get; set; }
    }
}
