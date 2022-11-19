namespace NumbersGoUp.Models
{
    public class TickerPosition
    {
        public Position Position { get; set; }
        public Ticker Ticker { get; set; }
    }
    public class BuySellState
    {
        public BarMetric BarMetric { get; set; }
        public double Multiplier { get; set; }
        public TickerPosition TickerPosition { get; set; }
        public double ProfitLossPerc { get; set; }
    }
}
