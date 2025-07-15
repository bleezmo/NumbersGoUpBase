using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUpBase.JsonModels
{
    public class SchwabOrder
    {
        public string OrderType { get; set; }
        public string Session {  get; set; }
        public string Duration { get; set; }
        public string OrderStrategyType { get; set; }
        public double Price { get; set; }
        public SchwabOrderLeg[] OrderLegCollection { get; set; }

        public static SchwabOrder DefaultLimitBuy(double limit, double qty, string symbol) => DefaultLimit("BUY", limit, qty, symbol);
        public static SchwabOrder DefaultLimitSell(double limit, double qty, string symbol) => DefaultLimit("SELL", limit, qty, symbol);
        public static SchwabOrder DefaultMarketBuy(double qty, string symbol) => DefaultMarket("BUY", qty, symbol);
        public static SchwabOrder DefaultMarketSell(double qty, string symbol) => DefaultMarket("SELL", qty, symbol);
        private static SchwabOrder DefaultLimit(string instruction, double price, double qty, string symbol) => new SchwabOrder
        {
            OrderType = "LIMIT",
            Session = "NORMAL",
            Duration = "DAY",
            OrderStrategyType = "SINGLE",
            Price = price,
            OrderLegCollection =
            [
                SchwabOrderLeg.Default(instruction, qty, symbol)
            ]
        };
        private static SchwabOrder DefaultMarket(string instruction, double qty, string symbol) => new SchwabOrder
        {
            OrderType = "MARKET",
            Session = "NORMAL",
            Duration = "DAY",
            OrderStrategyType = "SINGLE",
            OrderLegCollection =
            [
                SchwabOrderLeg.Default(instruction, qty, symbol)
            ]
        };
    }
    public class SchwabOrderLeg
    {
        public string Instruction { get; set; }
        public double Quantity { get; set; }
        public SchwabOrderInstrument Instrument { get; set; }

        public static SchwabOrderLeg Default(string instruction, double qty, string symbol) => new SchwabOrderLeg
        {
            Instruction = instruction,
            Quantity = qty,
            Instrument = SchwabOrderInstrument.Default(symbol)
        };
    }
    public class SchwabOrderInstrument
    {
        public string Symbol { get; set; }
        public string AssetType { get; set; }

        public static SchwabOrderInstrument Default(string symbol) => new SchwabOrderInstrument
        {
            Symbol = symbol,
            AssetType = "EQUITY"
        };
    }
}
