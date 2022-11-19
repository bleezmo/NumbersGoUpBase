using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Models
{
    public class MarketDay
    {
        public DateTime TradingTimeOpen { get; set; }
        public DateTime TradingTimeClose { get; set; }
        public DateTime Date => TradingTimeOpen.Date;
    }
}
