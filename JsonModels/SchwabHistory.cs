using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUpBase.JsonModels
{
    public class SchwabHistory
    {
        public SchwabHistoryBar[] Candles {  get; set; }
        public string Symbol { get; set; }
        public bool Empty { get; set; }
    }
    public class SchwabHistoryBar
    {
        public double? Open { get; set; }
        public double? Close { get; set; }
        public double? High { get; set; }
        public double? Low { get; set; }
        public long Volume { get; set; }
        public long? Datetime { get; set; }

        public bool IsInvalid() => new[] {Open, High, Low, Close, Datetime}.Any(x => !x.HasValue);
    }
}
