using System;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace NumbersGoUp.Models
{
    [Index(nameof(Symbol))]
    [Index(nameof(BarDayMilliseconds))]
    [Index(nameof(TickerId))]
    public class HistoryBar
    {
        public long Id { get; set; }
        public string Symbol { get; set; }
        public double OpenPrice { get; set; }
        public double ClosePrice { get; set; }
        public double HighPrice { get; set; }
        public double LowPrice { get; set; }
        public long Volume { get; set; }
        public DateTime BarDay { get; set; }
        public long BarDayMilliseconds { get; set; }
        public long TickerId { get; set; }
        [ForeignKey("TickerId")]
        public Ticker Ticker { get; set; }
        public BarMetric BarMetric { get; set; }
    }
    public static class HistoryBarExtensions
    {
        public static double Price(this HistoryBar bar)
        {
            return (bar.ClosePrice + bar.HighPrice + bar.LowPrice + bar.OpenPrice) / 4;
        }
        public static double HLC3(this HistoryBar bar)
        {
            return (bar.ClosePrice + bar.HighPrice + bar.LowPrice) / 3;
        }
    }
}
