using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Models
{
    public class BrokerOrder
    {
        public string BrokerOrderId { get; set; }

        public string ClientOrderId { get; set; }

        public DateTime? FilledAt { get; set; }

        public string Symbol { get; set; }

        public double FilledQuantity { get; set; }

        public OrderSide OrderSide { get; set; }

        public double? AverageFillPrice { get; set; }
        public OrderStatus OrderStatus { get; set; }
    }
    public enum OrderSide
    {
        Buy = 0,
        Sell = 1
    }
    public enum OrderStatus
    {
        NOT_FILLED = 0,
        PARTIALLY_FILLED = 1,
        FILLED = 2
    }
}
