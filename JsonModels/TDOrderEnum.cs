using System;
using System.Collections.Generic;
using System.Text;

namespace NumbersGoUp.JsonModels
{
    public enum OrderType
    {
        MARKET, LIMIT, STOP, STOP_LIMIT, TRAILING_STOP, MARKET_ON_CLOSE, EXERCISE, TRAILING_STOP_LIMIT, NET_DEBIT, NET_CREDIT, NET_ZERO
    }
    public enum TDSession
    {
        NORMAL, AM, PM, SEAMLESS
    }
    public enum Duration
    {
        DAY, GOOD_TILL_CANCEL, FILL_OR_KILL
    }
    public enum OrderStrategyType
    {
        SINGLE, OCO, TRIGGER
    }
    public enum Instruction
    {
        BUY, SELL, BUY_TO_COVER, SELL_SHORT, BUY_TO_OPEN, BUY_TO_CLOSE, SELL_TO_OPEN, SELL_TO_CLOSE, EXCHANGE,
    }
    public enum AssetType
    {
        EQUITY, OPTION, INDEX, MUTUAL_FUND, CASH_EQUIVALENT, FIXED_INCOME, CURRENCY
    }
    public enum Status
    {
        AWAITING_PARENT_ORDER,
        AWAITING_CONDITION,
        AWAITING_MANUAL_REVIEW,
        ACCEPTED,
        AWAITING_UR_OUT,
        PENDING_ACTIVATION,
        QUEUED,
        WORKING,
        REJECTED,
        PENDING_CANCEL,
        CANCELED,
        PENDING_REPLACE,
        REPLACED,
        FILLED,
        EXPIRED
    }
}
