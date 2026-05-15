using System;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace NumbersGoUp.Models
{
    [Index(nameof(HistoryBarId), IsUnique = true)]
    [Index(nameof(Symbol))]
    [Index(nameof(BarDayMilliseconds))]
    public class BarMetric
    {
        public long Id { get; set; }
        public string Symbol { get; set; }
        public DateTime BarDay { get; set; }
        public long BarDayMilliseconds { get; set; }
        public long HistoryBarId { get; set; }
        [ForeignKey("HistoryBarId")]
        public HistoryBar HistoryBar { get; set; }
        public double AlmaSMA { get; set; }
        public double SMASMA { get; set; }
        public double SMA2SMA { get; set; }
        public double ProfitLossPerc { get; set; }
        public double WeekTrend { get; set; }
        public double VolAlmaSMA { get; set; }
        public double VolatilitySMA { get; set; }
        public double Volatility { get; set; }
    }
}
