using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Models
{
    public class Position
    {

        public string Symbol { get; set; }

        public double? AverageEntryPrice { get; set; }

        public double Quantity { get; set; }

        public double? MarketValue { get; set; }

        public double CostBasis { get; set; }

        public double? UnrealizedProfitLoss { get; set; }

        public double? UnrealizedProfitLossPercent { get; set; }

        public double? AssetCurrentPrice { get; set; }

        public double? AssetLastPrice { get; set; }
    }
}
