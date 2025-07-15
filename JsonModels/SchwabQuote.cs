using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUpBase.JsonModels
{
    public class SchwabQuote
    {
        public double? LastPrice { get; set; }
        public long LastSize { get; set; }
        public long? TradeTime { get; set; }

        public bool IsInvalid() => LastPrice == null || TradeTime == null;
    }
}
