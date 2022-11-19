using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Models
{
    public class Account
    {
        private bool _active = true;
        public bool IsActive { get => _active; set => _active = value; }
        public string AccountId { get; set; }
        public Balance Balance { get; set; }
    }
    public class Balance
    {
        public double TradableCash { get; set; }

        public double? BuyingPower { get; set; }

        public double LastEquity { get; set; }
    }
}
