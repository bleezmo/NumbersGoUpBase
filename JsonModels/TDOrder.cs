using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Text;

namespace NumbersGoUp.JsonModels
{

    /**
     * {
     *   "orderType": "LIMIT",
     *   "session": "NORMAL",
     *   "price": "34.97",
     *   "duration": "DAY",
     *   "orderStrategyType": "TRIGGER",
     *   "orderLegCollection": [
     *     {
     *       "instruction": "BUY",
     *       "quantity": 1,
     *       "instrument": {
     *         "symbol": "VZ",
     *         "assetType": "EQUITY"
     *       }
     *     }
     *   ],
     *   "childOrderStrategies": [
     *     {
     *       "orderType": "LIMIT",
     *       "session": "NORMAL",
     *       "price": "42.03",
     *       "duration": "DAY",
     *       "orderStrategyType": "SINGLE",
     *       "orderLegCollection": [
     *         {
     *           "instruction": "SELL",
     *           "quantity": 1,
     *           "instrument": {
     *             "symbol": "VZ",
     *             "assetType": "EQUITY"
     *           }
     *         }
     *       ]
     *     }
     *   ]
     * }
     */
    public class TDOrder
    {

        [JsonConverter(typeof(StringEnumConverter))]
        public OrderType? OrderType { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public TDSession? Session { get; set; }
        public decimal? Price { get; set; }
        public decimal? StopPrice { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public Duration? Duration { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public OrderStrategyType OrderStrategyType { get; set; }

        public IList<OrderLeg> OrderLegCollection { get; set; }

        public static TDOrder Buy(string symbol, decimal price, int quantity, Duration? duration)
        {
            return new TDOrder
            {
                OrderType = JsonModels.OrderType.LIMIT,
                Session = JsonModels.TDSession.NORMAL,
                Price = price,
                Duration = duration,
                OrderStrategyType = OrderStrategyType.SINGLE,
                OrderLegCollection = new[]
                {
                    new OrderLeg
                    {
                        Instruction = Instruction.BUY,
                        Quantity = quantity,
                        Instrument = new Instrument
                        {
                            AssetType = AssetType.EQUITY,
                            Symbol = symbol
                        }
                    }
                }
            };
        }

        public static TDOrder Sell(string symbol, decimal price, int quantity, Duration? duration)
        {
            return new TDOrder
            {
                OrderType = JsonModels.OrderType.LIMIT,
                Session = JsonModels.TDSession.NORMAL,
                Price = price,
                Duration = duration,
                OrderStrategyType = OrderStrategyType.SINGLE,
                OrderLegCollection = new[]
                {
                    new OrderLeg
                    {
                        Instruction = Instruction.SELL,
                        Quantity = quantity,
                        Instrument = new Instrument
                        {
                            AssetType = AssetType.EQUITY,
                            Symbol = symbol
                        }
                    }
                }
            };
        }

        public static TDOrder Stop(string symbol, decimal stopPrice, int quantity, Duration? duration)
        {
            return new TDOrder
            {
                OrderType = JsonModels.OrderType.STOP,
                Session = JsonModels.TDSession.NORMAL,
                StopPrice = stopPrice,
                Duration = duration,
                OrderStrategyType = OrderStrategyType.SINGLE,
                OrderLegCollection = new[]
                {
                    new OrderLeg
                    {
                        Instruction = Instruction.SELL,
                        Quantity = quantity,
                        Instrument = new Instrument
                        {
                            AssetType = AssetType.EQUITY,
                            Symbol = symbol
                        }
                    }
                }
            };
        }
    }
    public class OrderLeg
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public Instruction Instruction { get; set; }

        public double Quantity { get; set; }

        public Instrument Instrument { get; set; }
    }
    public class Instrument
    {
        public string Symbol { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public AssetType AssetType { get; set; }
    }
}
