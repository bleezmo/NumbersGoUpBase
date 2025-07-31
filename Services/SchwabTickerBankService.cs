using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NumbersGoUp.Models;
using NumbersGoUp.Services;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUpBase.Services
{
    public class SchwabTickerBankService : ITickerBankService
    {
        public const DayOfWeek RUN_TICKERBANK = DayOfWeek.Monday;

        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<SchwabTickerBankService> _logger;
        private readonly ITickerPickProcessor _tickerPickProcessor;
        private readonly IStocksContextFactory _contextFactory;
        private readonly int _lookbackYears;
        private readonly DateTime _lookbackDate;
        private readonly IRuntimeSettings _runtimeSettings;
        private readonly SchwabService _brokerService;

        public SchwabTickerBankService(IConfiguration configuration, IStocksContextFactory contextFactory, IRuntimeSettings runtimeSettings, ITickerPickProcessor tickerPickProcessor,
                                IAppCancellation appCancellation, ILogger<SchwabTickerBankService> logger, IBrokerService brokerService)
        {
            _appCancellation = appCancellation;
            _logger = logger;
            _tickerPickProcessor = tickerPickProcessor;
            _contextFactory = contextFactory;
            _brokerService = brokerService as SchwabService;
            _runtimeSettings = runtimeSettings;
            _lookbackYears = runtimeSettings.LookbackYears;
            _lookbackDate = DateTime.Now.AddYears(-_lookbackYears);
        }
        public async Task Load()
        {
            if(!_runtimeSettings.ForceDataCollection && DateTime.Now.DayOfWeek != RUN_TICKERBANK)
            {
                return;
            }
            await _brokerService.Ready();
            _logger.LogInformation("Starting bank ticker imports and calculations");
            try
            {
                await LoadBankTickers();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error occurred when loading ticker bank data");
            }
            await CalculatePerformance();
            _logger.LogInformation("Completed bank ticker imports and calculations");
        }
        private async Task LoadBankTickers()
        {
            var tickerPicks = await _tickerPickProcessor.GetTickers();
            var positions = await _brokerService.GetPositions();
            var symbols = tickerPicks.Select(t => t.Symbol).Concat(positions.Where(p => !tickerPicks.Any(t => t.Symbol == p.Symbol)).Select(p => p.Symbol)).ToArray();
            var fundamentals = await _brokerService.GetSchwabFinancials(symbols);
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var dbTickers = await stocksContext.TickerBank.ToArrayAsync(_appCancellation.Token);
                List<BankTicker> toUpdate = new List<BankTicker>(), toAdd = new List<BankTicker>(), toRemove = new List<BankTicker>();
                DateTime now = DateTime.UtcNow;
                foreach (var fundamental in fundamentals)
                {
                    var dbTicker = dbTickers.FirstOrDefault(t => t.Symbol == fundamental.Symbol);
                    try
                    {
                        var isNew = dbTicker == null;
                        if (isNew)
                        {
                            dbTicker = new BankTicker
                            {
                                Symbol = fundamental.Symbol
                            };
                        }
                        await PopulatePriceChangeAvg(stocksContext, dbTicker);
                        dbTicker.LastCalculatedFinancials = now;
                        dbTicker.LastCalculatedFinancialsMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                        dbTicker.DebtEquityRatio = fundamental.TotalDebtToEquity / 100;
                        dbTicker.DividendYield = fundamental.DividendYield / 100;
                        dbTicker.CurrentRatio = fundamental.CurrentRatio;
                        dbTicker.Earnings = fundamental.EpsTTM * fundamental.SharesOutstanding;
                        dbTicker.EPS = fundamental.EpsTTM;
                        dbTicker.MarketCap = fundamental.MarketCap;
                        dbTicker.PERatio = fundamental.PeRatio;
                        dbTicker.Shares = fundamental.SharesOutstanding;
                        if (isNew) { toAdd.Add(dbTicker); }
                        else { toUpdate.Add(dbTicker); }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, $"Error loading info for bank ticker {fundamental.Symbol}");
                    }
                }
                foreach (var dbTicker in dbTickers)
                {
                    if (!symbols.Any(s => s == dbTicker.Symbol))
                    {
                        toRemove.Add(dbTicker);
                    }
                    else if(!fundamentals.Any(f => f.Symbol == dbTicker.Symbol))
                    {
                        //if fundamentals aren't found, force performancevector to 0
                        dbTicker.EPS = 0;
                        dbTicker.Earnings = 0;
                        toUpdate.Add(dbTicker);
                    }
                }
                stocksContext.TickerBank.RemoveRange(toRemove);
                await stocksContext.SaveChangesAsync(_appCancellation.Token);
                stocksContext.TickerBank.UpdateRange(toUpdate);
                await stocksContext.SaveChangesAsync(_appCancellation.Token);
                stocksContext.TickerBank.AddRange(toAdd);
                await stocksContext.SaveChangesAsync(_appCancellation.Token);
            }
        }
        private async Task CalculatePerformance()
        {
            var now = DateTime.UtcNow;
            var nowMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var dbTickers = await stocksContext.TickerBank.ToArrayAsync(_appCancellation.Token);
                var tickers = new List<BankTicker>();
                foreach (var t in dbTickers)
                {
                    if (t.EPS > 0 && t.Earnings > 0 && t.PriceChangeAvg > -10 && t.DebtEquityRatio < 10)
                    {
                        tickers.Add(t);
                    }
                    else
                    {
                        t.LastCalculatedPerformance = now;
                        t.LastCalculatedPerformanceMillis = nowMillis;
                        t.PerformanceVector = 0;
                        stocksContext.TickerBank.Update(t);
                    }
                }
                await stocksContext.SaveChangesAsync(_appCancellation.Token);
                Func<BankTicker, double> performanceFn1 = (t) => Math.Sqrt(t.Earnings);
                Func<BankTicker, double> performanceFn2 = (t) => t.EPS; 
                Func<BankTicker, double> performanceFn3 = (t) => t.PriceChangeAvg;
                Func<BankTicker, double> performanceFn4 = (t) => Math.Max(t.CurrentRatio, 0);
                Func<BankTicker, double> performanceFn5 = (t) => t.DividendYield.ZeroReduceSlow(0.06, 0);
                Func<BankTicker, double> performanceRFn1 = (t) => Math.Max(t.DebtEquityRatio, 0);
                var minmax1 = new MinMaxStore<BankTicker>(performanceFn1);
                var minmax2 = new MinMaxStore<BankTicker>(performanceFn2);
                var minmax3 = new MinMaxStore<BankTicker>(performanceFn3);
                var minmax4 = new MinMaxStore<BankTicker>(performanceFn4);
                var minmax5 = new MinMaxStore<BankTicker>(performanceFn5);
                var minmax6 = new MinMaxStore<BankTicker>(performanceRFn1);
                foreach (var ticker in tickers)
                {
                    minmax1.Run(ticker);
                    minmax2.Run(ticker);
                    minmax3.Run(ticker);
                    minmax4.Run(ticker);
                    minmax5.Run(ticker);
                    minmax6.Run(ticker);
                }
                Func<BankTicker, double> performanceFnTotal = (t) => (performanceFn1(t).DoubleReduce(minmax1.Max, minmax1.Min) * 30) +
                                                                     (performanceFn2(t).DoubleReduce(minmax2.Max, minmax2.Min) * 10) +
                                                                     (performanceFn3(t).DoubleReduce(minmax3.Max, minmax3.Min) * 40) +
                                                                     (performanceFn4(t).DoubleReduce(minmax4.Max, minmax4.Min) * 5) +
                                                                     (performanceFn5(t).DoubleReduce(minmax5.Max, minmax5.Min) * 5) +
                                                                     ((1 - performanceRFn1(t).DoubleReduce(minmax6.Max, minmax6.Min)) * 10);
                var minmaxTotal = new MinMaxStore<BankTicker>(performanceFnTotal);
                foreach (var ticker in tickers)
                {
                    minmaxTotal.Run(ticker);
                }
                foreach (var ticker in tickers)
                {
                    ticker.LastCalculatedPerformance = now;
                    ticker.LastCalculatedPerformanceMillis = nowMillis;
                    ticker.PerformanceVector = performanceFnTotal(ticker).DoubleReduce(minmaxTotal.Max, minmaxTotal.Min) * 100;
                    stocksContext.TickerBank.Update(ticker);
                }
                await stocksContext.SaveChangesAsync(_appCancellation.Token);
            }
        }
        private async Task PopulatePriceChangeAvg(StocksContext stocksContext, BankTicker ticker)
        {
            var bars = await stocksContext.HistoryBars.Where(b => b.Symbol == ticker.Symbol).OrderBy(b => b.BarDayMilliseconds).ToArrayAsync(_appCancellation.Token);
            var priceChangeAvg = CalculatePriceChangeAvg(bars.Length > 0 ? bars : (await _brokerService.GetBarHistoryDay(ticker.Symbol, _lookbackDate)).OrderBy(b => b.BarDayMilliseconds).ToArray());
            ticker.PriceChangeAvg = priceChangeAvg ?? -10;
        }
        private double? CalculatePriceChangeAvg(HistoryBar[] barsAsc)
        {
            if (barsAsc == null || barsAsc.Length == 0) { return null; }
            var cutoff = _lookbackDate.AddMonths(6);
            if (barsAsc[0].BarDay.CompareTo(cutoff) > 0)
            {
                return null;
            }
            var initialInitialPrice = barsAsc[0].Price();
            var (totalslope, totalyintercept) = barsAsc.CalculateRegression(b => (b.Price() - initialInitialPrice) * 100.0 / initialInitialPrice);
            var regressionTotal = (totalslope * barsAsc.Length) + totalyintercept;
            var stdevTotal = barsAsc.RegressionStDev(b => (b.Price() - initialInitialPrice) * 100.0 / initialInitialPrice, totalslope, totalyintercept);
            const int interval = 120;
            const int minLength = interval / 2;
            var priceChanges = new Stack<double>();
            for (var i = 0; i < barsAsc.Length; i += interval)
            {
                var priceWindow = barsAsc.Skip(i).Take(interval).ToArray();
                var initialPrice = priceWindow[0].Price();
                if (priceWindow.Length > minLength && initialPrice > 0 && priceWindow.Last().Price() > 0)
                {
                    var (slope, yintercept) = priceWindow.CalculateRegression(b => (b.Price() - initialPrice) * 100.0 / initialPrice);
                    var price = (slope * priceWindow.Length) + yintercept;
                    var stdev = priceWindow.RegressionStDev(b => (b.Price() - initialPrice) * 100.0 / initialPrice, slope, yintercept);
                    if (stdev > 0)
                    {
                        priceChanges.Push(price / stdev);
                    }
                    else
                    {
                        _logger.LogWarning($"price change avg standard deviation was zero for {barsAsc[0].Symbol}");
                    }
                }
            }
            if (priceChanges.Count > 3 && priceChanges.Any())
            {
                return Math.Min(priceChanges.Average(), regressionTotal / stdevTotal);
            }
            else
            {
                _logger.LogDebug($"Insufficient price information for {barsAsc[0].Symbol}");
                return null;
            }
        }
    }
}
