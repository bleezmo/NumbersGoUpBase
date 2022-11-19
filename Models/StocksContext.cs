using Microsoft.EntityFrameworkCore;

namespace NumbersGoUp.Models
{
    public abstract class StocksContext : DbContext
    {
        public StocksContext(DbContextOptions options) : base(options) { }
        public virtual DbSet<HistoryBar> HistoryBars { get; set; }
        public virtual DbSet<BarMetric> BarMetrics { get; set; }
        public virtual DbSet<Ticker> Tickers { get; set; }
        public virtual DbSet<DbOrder> Orders { get; set; }
        public virtual DbSet<DbOrderHistory> OrderHistories { get; set; }
        public virtual DbSet<BankTicker> TickerBank { get; set; }
    }
    public interface IStocksContextFactory : IDbContextFactory<StocksContext>
    {
    }
}
