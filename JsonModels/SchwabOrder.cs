using NumbersGoUp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUpBase.JsonModels
{
    public class SchwabOrder
    {
        public long OrderId { get; set; }
        public string OrderType { get; set; }
        public string Session {  get; set; }
        public string Duration { get; set; }
        public string OrderStrategyType { get; set; }
        public double Price { get; set; }
        public string Status { get; set; }
        public string CloseTime { get; set; }
        public double? Quantity { get; set; }
        public double? FilledQuantity { get; set; }
        public double? RemainingQuantity { get; set; }
        public SchwabOrderLeg[] OrderLegCollection { get; set; }
        public SchwabOrderActivity[] OrderActivityCollection { get; set; }

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

    public class SchwabOrderActivity
    {
        public double OrderRemainingQuantity { get; set; }
        public SchwabOrderExecutionLeg[] ExecutionLegs { get; set; }
    }
    public class SchwabOrderExecutionLeg
    {
        public double Quantity { get; set; }
        public double Price { get; set; }
        public string Time { get; set; }
    }
    public class SchwabOrderStatus
    {
        public const string AWAITING_PARENT_ORDER = "AWAITING_PARENT_ORDER";
        public const string AWAITING_CONDITION = "AWAITING_CONDITION";
        public const string AWAITING_STOP_CONDITION = "AWAITING_STOP_CONDITION";
        public const string AWAITING_MANUAL_REVIEW = "AWAITING_MANUAL_REVIEW";
        public const string ACCEPTED = "ACCEPTED";
        public const string AWAITING_UR_OUT = "AWAITING_UR_OUT";
        public const string PENDING_ACTIVATION = "PENDING_ACTIVATION";
        public const string QUEUED = "QUEUED";
        public const string WORKING = "WORKING";
        public const string REJECTED = "REJECTED";
        public const string PENDING_CANCEL = "PENDING_CANCEL";
        public const string CANCELED = "CANCELED";
        public const string PENDING_REPLACE = "PENDING_REPLACE";
        public const string REPLACED = "REPLACED";
        public const string FILLED = "FILLED";
        public const string EXPIRED = "EXPIRED";
        public const string NEW = "NEW";
        public const string AWAITING_RELEASE_TIME = "AWAITING_RELEASE_TIME";
        public const string PENDING_ACKNOWLEDGEMENT = "PENDING_ACKNOWLEDGEMENT";
        public const string PENDING_RECALL = "PENDING_RECALL";
        public const string UNKNOWN = "UNKNOWN";
    }
    public class SchwabOrderInstruction
    {
        public const string BUY = "BUY";
        public const string SELL = "SELL";
        public const string BUY_TO_COVER = "BUY_TO_COVER";
        public const string SELL_SHORT = "SELL_SHORT";
        public const string BUY_TO_OPEN = "BUY_TO_OPEN";
        public const string BUY_TO_CLOSE = "BUY_TO_CLOSE";
        public const string SELL_TO_OPEN = "SELL_TO_OPEN";
        public const string SELL_TO_CLOSE = "SELL_TO_CLOSE";
        public const string EXCHANGE = "EXCHANGE";
        public const string SELL_SHORT_EXEMPT = "SELL_SHORT_EXEMPT";
    }
    public static class SchwabOrderExtensions
    {
        public static (BrokerOrder, string) ToBrokerOrder(this SchwabOrder order)
        {
            var orderLeg = order.OrderLegCollection?.FirstOrDefault();
            if (orderLeg == null)
            {
                return (null, "Error retrieving order. Order leg not found.");
            }
            if (orderLeg.Instrument == null)
            {
                return (null, "Error retrieving order. Order instrument not found. No ticker symbol available.");
            }
            OrderSide? orderSide = orderLeg.Instruction == SchwabOrderInstruction.BUY ? OrderSide.Buy : (orderLeg.Instruction == SchwabOrderInstruction.SELL ? OrderSide.Sell : null);
            if (orderSide == null)
            {
                return (null, $"Error retrieving order. Invalid order instruction: {orderLeg.Instruction}");
            }
            var brokerOrder = new BrokerOrder
            {
                BrokerOrderId = order.OrderId.ToString(),
                OrderSide = orderSide.Value,
                Symbol = orderLeg.Instrument.Symbol,
            };
            if (order.Status == SchwabOrderStatus.FILLED)
            {
                var executionLeg = order.OrderActivityCollection?.FirstOrDefault()?.ExecutionLegs?.FirstOrDefault();
                brokerOrder.AverageFillPrice = executionLeg?.Price ?? order.Price;
                brokerOrder.FilledAt = DateTime.TryParse(order.CloseTime ?? executionLeg?.Time, out var filledAt) ? filledAt : null;
                brokerOrder.FilledQuantity = order.FilledQuantity ?? executionLeg?.Quantity ?? order.Quantity ?? orderLeg.Quantity;
                brokerOrder.OrderStatus = OrderStatus.FILLED;
            }
            else
            {
                brokerOrder.OrderStatus = OrderStatus.NOT_FILLED;
            }
            return (brokerOrder, null);
        }
    }
}
