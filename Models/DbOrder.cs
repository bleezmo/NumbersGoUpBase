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
    [Index(nameof(TickerId))]
    [Index(nameof(TimeLocalMilliseconds))]
    public class DbOrder
    {
        public long Id { get; set; }
        public long TickerId { get; set; }
        [ForeignKey("TickerId")]
        public Ticker Ticker { get; set; }
        public string Symbol { get; set; }
        public double TargetPrice { get; set; }
        public double AppliedAmt { get; set; }
        public OrderSide Side { get; set; }
        public DateTime TimeLocal { get; set; }
        public long TimeLocalMilliseconds { get; set; }
        public double Multiplier { get; set; }
        public int DaysFromLastBuy { get; set; }
        public double AvgEntryPrice { get; set; }
        public string BrokerOrderId { get; set; }
        public string Account { get; set; }
    }
}
