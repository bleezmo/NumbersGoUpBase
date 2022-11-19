using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Models
{
    public class Quote
    {
        public string Symbol { get; set; }
        public double Price { get; set; }
        public long Size { get; set; }
        public DateTime TradeTime { get; set; }
        public long TradeTimeMilliseconds { get; set; }
    }
}
