using Microsoft.EntityFrameworkCore;

namespace NumbersGoUp.Models
{
    [Index(nameof(Sector))]
    [Index(nameof(BarDayMilliseconds))]
    public class SectorMetric
    {
        public long Id { get; set; }
        public string Sector { get; set; }
        public DateTime BarDay { get; set; }
        public long BarDayMilliseconds { get; set; }
        public double AlmaSMA1 { get; set; }
        public double AlmaSMA2 { get; set; }
        public double AlmaSMA3 { get; set; }
        public double SMASMA { get; set; }
        public double RegressionSlope { get; set; }
    }
}
