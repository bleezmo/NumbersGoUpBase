using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUpBase.JsonModels
{
    public class SchwabMarketsWrapper
    {
        public bool IsOpen { get; set; }
        public SchwabMarkets SessionHours { get; set; }
    }
    public class SchwabMarkets
    {
        public SchwabMarket[] PreMarket { get; set; }
        public SchwabMarket[] RegularMarket { get; set; }
        public SchwabMarket[] PostMarket { get; set; }

    }
    public class SchwabMarket
    {
        public string Start { get; set; }
        public string End { get; set; }
    }
}
