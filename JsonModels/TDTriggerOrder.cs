using System;
using System.Collections.Generic;
using System.Text;

namespace NumbersGoUp.JsonModels
{
    public class TDTriggerOrder : TDOrder
    {
        public IList<TDOrder> ChildOrderStrategies { get; set; }

        public static TDTriggerOrder Buy(string symbol, decimal buyPrice, decimal sellPrice, int quantity)
        {
            return new TDTriggerOrder
            {
                OrderType = JsonModels.OrderType.LIMIT,
                Session = JsonModels.TDSession.NORMAL,
                Price = buyPrice,
                Duration = JsonModels.Duration.DAY,
                OrderStrategyType = OrderStrategyType.TRIGGER,
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
                },
                ChildOrderStrategies = new[]
                {
                    new TDOrder
                    {
                        OrderType = JsonModels.OrderType.LIMIT,
                        Session = JsonModels.TDSession.NORMAL,
                        Price = sellPrice,
                        Duration = JsonModels.Duration.DAY,
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
                    }
                }
            };
        }
    }
}
