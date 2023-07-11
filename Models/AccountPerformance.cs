using Newtonsoft.Json;
using NumbersGoUp.JsonModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Models
{
    public class AccountHistoryEvent
    {
        public const string DividendType = "dividend";
        public const string TradeType = "trade";

        public double Amount { get; set; }
        public string TypeStr { get; set; }
        public double Price { get; set; }
        public double Qty { get; set; }
        public string Symbol { get; set; }
        public DateTime Date { get; set; }

    }
    public static class QueueEventDetailsExtension
    {
        public static void EnqueueEventDetails(this Queue<double> queue, AccountHistoryEvent eventDetails)
        {
            for(var i = 0; i < eventDetails.Qty; i++)
            {
                queue.Enqueue(eventDetails.Price);
            }
        }
    }
}
