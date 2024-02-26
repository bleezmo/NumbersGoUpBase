using Microsoft.Extensions.Configuration;
using NumbersGoUp.Models;
using NumbersGoUp.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace NumbersGoUp.Services
{
    public class TickerBankService
    {
        private const string EARNINGS_MULTIPLE_CUTOFF_KEY = "EarningsMultipleCutoff";

        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TickerBankService> _logger;
        private readonly ITickerBankProcessor _tickerProcessor;
        private readonly ITickerPickProcessor _tickerPickProcessor;
        private readonly IStocksContextFactory _contextFactory;
        private readonly IRuntimeSettings _runtimeSettings;
        private readonly int _lookbackYears;
        private readonly DateTime _lookbackDate;
        public double EarningsMultipleCutoff { get; }

        private readonly IBrokerService _brokerService;

        public TickerBankService(IConfiguration configuration, IStocksContextFactory contextFactory, IRuntimeSettings runtimeSettings, ITickerPickProcessor tickerPickProcessor,
                                IAppCancellation appCancellation, ILogger<TickerBankService> logger, ITickerBankProcessor tickerProcessor, IBrokerService brokerService)
        {
            _appCancellation = appCancellation;
            _logger = logger;
            _tickerProcessor = tickerProcessor;
            _tickerPickProcessor = tickerPickProcessor;
            _contextFactory = contextFactory;
            _runtimeSettings = runtimeSettings;
            EarningsMultipleCutoff = double.TryParse(configuration[EARNINGS_MULTIPLE_CUTOFF_KEY], out var peratioCutoff) ? peratioCutoff : 40;
            _brokerService = brokerService;
            _lookbackYears = runtimeSettings.LookbackYears;
            _lookbackDate = DateTime.Now.AddYears(-_lookbackYears);
        }
        public async Task Load()
        {
            try
            {
                var result = await _tickerProcessor.DownloadTickers(_runtimeSettings.ForceDataCollection);
                var processorTickers = result.BankTickers;
                if (processorTickers.Length == 0) { return; }
                var tickerPicks = await _tickerPickProcessor.GetTickers();
                _logger.LogInformation($"Loading {processorTickers.Length} tickers into ticker bank");
                var positions = await _brokerService.GetPositions();
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var dbTickers = await stocksContext.TickerBank.ToArrayAsync(_appCancellation.Token);
                    List<BankTicker> toUpdate = new List<BankTicker>(), toAdd = new List<BankTicker>(), toRemove = new List<BankTicker>();
                    DateTime now = DateTime.UtcNow;
                    foreach (var processorTicker in processorTickers)
                    {
                        var ticker = processorTicker.Ticker;
                        var dbTicker = dbTickers.FirstOrDefault(t => t.Symbol == ticker.Symbol);
                        //var lastModified = result.LastModified.HasValue ? result.LastModified.Value.DateTime : DateTime.UtcNow;
                        var isCarryover = IsCarryover(processorTicker);
                        var passCutoff = LoadCutoff(processorTicker);
                        var isTickerPick = tickerPicks.Any(t => t.Symbol == ticker.Symbol);
                        var hasPosition = positions.Any(p => p.Symbol == ticker.Symbol);
                        try
                        {
                            if (isCarryover)
                            {
                                if (dbTicker != null)
                                {
                                    _logger.LogInformation($"Missing values for {ticker.Symbol}. Using old values.");
                                    if (hasPosition || isTickerPick)
                                    {
                                        await PopulatePriceChangeAvg(stocksContext, dbTicker);
                                        toUpdate.Add(dbTicker);
                                    }
                                }
                            }
                            else if (hasPosition || (isTickerPick && passCutoff))
                            {
                                await PopulatePriceChangeAvg(stocksContext, ticker);
                                ticker.LastCalculatedFinancials = now;
                                ticker.LastCalculatedFinancialsMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
                                if (dbTicker != null)
                                {
                                    _tickerProcessor.FinalCalc(processorTicker, dbTicker);
                                    _tickerProcessor.UpdateBankTicker(dbTicker, ticker);
                                    toUpdate.Add(dbTicker);
                                }
                                else { toAdd.Add(ticker); }
                            }
                            else if (dbTicker != null)
                            {
                                toRemove.Add(dbTicker);
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, $"Error loading info for bank ticker {ticker.Symbol}");
                        }
                    }
                    foreach(var dbTicker in dbTickers)
                    {
                        var isTickerPick = tickerPicks.Any(t => t.Symbol == dbTicker.Symbol);
                        var hasPosition = positions.Any(p => p.Symbol == dbTicker.Symbol);
                        var isProcessorTicker = processorTickers.Any(t => t.Ticker.Symbol == dbTicker.Symbol);
                        if (!isProcessorTicker && !isTickerPick && !hasPosition)
                        {
                            toRemove.Add(dbTicker);
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
            catch (Exception e)
            {
                _logger.LogError(e, "Error occurred when loading ticker bank data");
            }
            _logger.LogInformation("Completed loading bank tickers");
            await CalculatePerformance();
            _logger.LogInformation("Completed bank ticker performance calculation");
        }
        private bool LoadCutoff(ProcessorBankTicker ticker) => ticker.Income > 50000000 && ticker.RevenueGrowth > -25 && ticker.IncomeGrowth > -75 && (ticker.Ticker.MarketCap > 3_500_000_000 || ticker.IncomeGrowth < 75) && BasicCutoff(ticker.Ticker);
        private bool BasicCutoff(BankTicker ticker) => ticker.MarketCap > 3_000_000_000 && ticker.Earnings > 0 && 
                                                       (ticker.DebtMinusCash / ticker.Earnings) < 15 && ticker.EPS > 0 && 
                                                       ticker.EVEarnings > 0 && ticker.EVEarnings < EarningsMultipleCutoff;
        private bool IsCarryover(ProcessorBankTicker ticker) => ticker.Ticker.EVEarnings == 0 || ticker.Ticker.EPS == 0 || !ticker.RevenueGrowth.HasValue || 
                                                                !ticker.Income.HasValue || ticker.Ticker.MarketCap == 0 || !ticker.IncomeGrowth.HasValue;
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
                    if (BasicCutoff(t) && t.PERatio < EarningsMultipleCutoff && t.PERatio > 1 && t.PriceChangeAvg > 0)
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
                Func<BankTicker, double> performanceFn2 = (t) => t.PriceChangeAvg;
                Func<BankTicker, double> performanceFn3 = (t) => Math.Min(t.DividendYield, 0.06);
                Func<BankTicker, double> performanceFn4 = (t) => 1 - t.EVEarnings.DoubleReduce(EarningsMultipleCutoff, 0);
                Func<BankTicker, double> performanceFn5 = (t) => 1 - (t.DebtMinusCash / t.Earnings).DoubleReduce(10, -5);
                var minmax1 = new MinMaxStore<BankTicker>(performanceFn1);
                var minmax2 = new MinMaxStore<BankTicker>(performanceFn2);
                var minmax3 = new MinMaxStore<BankTicker>(performanceFn3);
                var minmax4 = new MinMaxStore<BankTicker>(performanceFn4);
                var minmax5 = new MinMaxStore<BankTicker>(performanceFn5);
                foreach (var ticker in tickers)
                {
                    minmax1.Run(ticker);
                    minmax2.Run(ticker);
                    minmax3.Run(ticker);
                    minmax4.Run(ticker);
                    minmax5.Run(ticker);
                }
                Func<BankTicker, double> performanceFnTotal = (t) => (performanceFn1(t).DoubleReduce(minmax1.Max, minmax1.Min) * 45) +
                                                                     (performanceFn2(t).DoubleReduce(minmax2.Max, minmax2.Min) * 20) +
                                                                     (performanceFn3(t).DoubleReduce(minmax3.Max, minmax3.Min) * 10) +
                                                                     (performanceFn4(t).DoubleReduce(minmax4.Max, minmax4.Min) * 10) +
                                                                     (performanceFn5(t).DoubleReduce(minmax5.Max, minmax5.Min) * 15);
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
            ticker.PriceChangeAvg = priceChangeAvg ?? 0.0;
        }
        private double? CalculatePriceChangeAvg(HistoryBar[] barsAsc)
        {
            if(barsAsc == null || barsAsc.Length == 0) { return null; }
            var cutoff = _lookbackDate.AddMonths(6);
            if (barsAsc[0].BarDay.CompareTo(cutoff) > 0)
            {
                return null;
            }
            var initialInitialPrice = barsAsc[0].Price();
            var (totalslope, totalyintercept) = barsAsc.CalculateRegression(b => (b.Price() - initialInitialPrice) * 100.0 / initialInitialPrice);
            var regressionTotal = (totalslope * barsAsc.Length) + totalyintercept;
            var stdevTotal = barsAsc.RegressionStDev(b => (b.Price() - initialInitialPrice) * 100.0 / initialInitialPrice, totalslope, totalyintercept);
            if (regressionTotal < 0) { return regressionTotal / stdevTotal; }
            const int interval = 120;
            const int minLength = interval / 2;
            var priceChanges = new Stack<double>();
            for(var i = 0; i < barsAsc.Length; i += interval)
            {
                var priceWindow = barsAsc.Skip(i).Take(interval).ToArray();
                var initialPrice = priceWindow[0].Price();
                if (priceWindow.Length > minLength && initialPrice > 0 && priceWindow.Last().Price() > 0)
                {
                    var (slope, yintercept) = priceWindow.CalculateRegression(b => (b.Price() - initialPrice) * 100.0 / initialPrice);
                    var price = (slope * priceWindow.Length) + yintercept;
                    var stdev = priceWindow.RegressionStDev(b => (b.Price() - initialPrice) * 100.0 / initialPrice, slope, yintercept);
                    if(stdev > 0)
                    {
                        priceChanges.Push(price / stdev);
                    }
                    else
                    {
                        _logger.LogWarning($"price change avg standard deviation was zero for {barsAsc[0].Symbol}");
                        return null;
                    }
                }
            }
            if (priceChanges.Count > 3 && priceChanges.Any())
            {
                return Math.Min(priceChanges.ToArray().ApplyAlma(), regressionTotal / stdevTotal);
            }
            else
            {
                _logger.LogDebug($"Insufficient price information for {barsAsc[0].Symbol}");
                return null;
            }
        }
    }
}
