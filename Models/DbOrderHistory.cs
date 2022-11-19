using Alpaca.Markets;
using Microsoft.EntityFrameworkCore;
using NumbersGoUp.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

namespace NumbersGoUp.Models
{
    [Index(nameof(Symbol))]
    [Index(nameof(TimeLocalMilliseconds))]
    public class DbOrderHistory
    {
        public long Id { get; set; }
        public string Symbol { get; set; }
        public OrderSide Side { get; set; }
        public double AvgFillPrice { get; set; }
        public double FillQty { get; set; }
        public DateTime TimeLocal { get; set; }
        public long TimeLocalMilliseconds { get; set; }
        public DateTime? NextBuy { get; set; }
        public DateTime? NextSell { get; set; }
        public double ProfitLossPerc { get; set; }
        public string Account { get; set; }
    }
}
