
namespace NumbersGoUp.Models
{
    public class TickerInfo
    {
        public string Symbol { get; set; }

        public string Name { get; set; }

        public bool IsTradable { get; set; }

        public bool Marginable { get; set; }

        public bool Shortable { get; set; }

        public bool EasyToBorrow { get; set; }

        public bool Fractionable { get; set; }
    }
}
