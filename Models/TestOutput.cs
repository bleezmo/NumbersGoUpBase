using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Models
{
    [Index(nameof(Symbol))]
    [Index(nameof(TimeUtcMilliseconds))]
    public class TestOutput
    {
        public long Id { get; set; }
        public string Symbol { get; set; }
        public DateTime TimeUtc { get; set; }
        public long TimeUtcMilliseconds { get; set; }
        public double SMA { get; set; }
        public double SMAUpper { get; set; }
        public double SMALower { get; set; }
        public double Alma { get; set; }
        public double VolEMA { get; set; }
        public double MaxPercSlope { get; set; }
        public double MinPercSlope { get; set; }
        public int TimeSection { get; set; }
        public double RSI { get; set; }
        public double StochasticOscillator { get; set; }
        public double OpenPrice { get; set; }
        public double ClosePrice { get; set; }
        public double HighPrice { get; set; }
        public double LowPrice { get; set; }
        public long Volume { get; set; }
        public double? BuyAmt { get; set; }
        public double? BuyPrice { get; set; }
        public double? SellAmt { get; set; }
        public double? SellPrice { get; set; }
    }
}
