using Microsoft.Extensions.Configuration;
using NumbersGoUp.Models;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.EntityFrameworkCore;
using NumbersGoUp.JsonModels;

namespace NumbersGoUp.Services
{
    public class TickerBankService
    {
        private const string PERATIO_CUTOFF_KEY = "PERatioCutoff";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TickerBankService> _logger;
        private readonly RateLimiter _rateLimiter;
        private readonly ITickerBankProcessor _tickerProcessor;
        private readonly IConfiguration _configuration;
        private readonly string _environmentName;
        private readonly IStocksContextFactory _contextFactory;
        private readonly IRuntimeSettings _runtimeSettings;
        private readonly DateTime _lookbackDate = DateTime.Now.AddYears(-DataService.LOOKBACK_YEARS);
        public double PERatioCutoff { get; }

        private readonly IBrokerService _brokerService;

        public TickerBankService(IConfiguration configuration, IHostEnvironment environment, IHttpClientFactory httpClientFactory, IStocksContextFactory contextFactory, IRuntimeSettings runtimeSettings,
                                IAppCancellation appCancellation, ILogger<TickerBankService> logger, RateLimiter rateLimiter, ITickerBankProcessor tickerProcessor, IBrokerService brokerService)
        {
            _httpClientFactory = httpClientFactory;
            _appCancellation = appCancellation;
            _logger = logger;
            _rateLimiter = rateLimiter;
            _environmentName = environment.EnvironmentName;
            _tickerProcessor = tickerProcessor;
            _configuration = configuration;
            _contextFactory = contextFactory;
            _runtimeSettings = runtimeSettings;
            PERatioCutoff = double.TryParse(configuration[PERATIO_CUTOFF_KEY], out var peratioCutoff) ? peratioCutoff : 30;
            _brokerService = brokerService;
        }
        public async Task Load()
        {
            try
            {
                var tickers = await _tickerProcessor.DownloadTickers(_runtimeSettings.ForceDataCollection);
                if (tickers.Count == 0) { return; }
                _logger.LogInformation($"Loading {tickers.Count} tickers into ticker bank");
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var dbTickers = await stocksContext.TickerBank.ToArrayAsync(_appCancellation.Token);
                    var toRemove = dbTickers.Where(t1 => !tickers.Any(t2 => t1.Symbol == t2.Symbol)).ToArray();
                    stocksContext.TickerBank.RemoveRange(toRemove);
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    List<BankTicker> toUpdate = new List<BankTicker>(), toAdd = new List<BankTicker>();
                    foreach (var bankTicker in tickers)
                    {
                        var bars = await stocksContext.HistoryBars.Where(b => b.Symbol == bankTicker.Symbol).ToArrayAsync();
                        bankTicker.PriceChangeAvg = CalculatePriceChangeAvg(bars.Length > 0 ? bars : (await _brokerService.GetBarHistoryDay(bankTicker.Symbol, _lookbackDate)).ToArray());
                        var dbTicker = dbTickers.FirstOrDefault(dbt => dbt.Symbol == bankTicker.Symbol);
                        if (dbTicker != null) {
                            _tickerProcessor.UpdateBankTicker(dbTicker, bankTicker);
                            toUpdate.Add(dbTicker);
                        }
                        else { toAdd.Add(bankTicker); }
                    }
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
        }
        public async Task CalculatePerformance()
        {
            var now = DateTime.UtcNow;
            var nowMillis = new DateTimeOffset(now).ToUnixTimeMilliseconds();
            var cutoff = new DateTimeOffset(now.AddDays(-15)).ToUnixTimeMilliseconds();
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var isUpdate = _runtimeSettings.ForceDataCollection || await stocksContext.TickerBank.Where(t => t.LastCalculatedPerformanceMillis == null || t.LastCalculatedPerformanceMillis < cutoff).AnyAsync(_appCancellation.Token);
                if (isUpdate)
                {
                    var dbTickers = await stocksContext.TickerBank.ToArrayAsync(_appCancellation.Token);
                    var tickers = new List<BankTicker>();
                    foreach(var t in dbTickers)
                    {
                        if(t.DebtEquityRatio > 0 && t.DebtEquityRatio < 1.5 && (t.CurrentRatio > 1.2 || t.DebtEquityRatio < 1) && 
                            t.Earnings > 0 && t.PERatio < PERatioCutoff && t.PERatio > 1 && t.DividendYield > 0.005 && t.PriceChangeAvg > 0 && t.EPS > 0)
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
                    Func<BankTicker, double> performanceFn1 = (t) => Math.Sqrt(t.Earnings) * 2;
                    Func<BankTicker, double> performanceFn2 = (t) => t.PriceChangeAvg;
                    Func<BankTicker, double> performanceFn3 = (t) => Math.Min(t.DividendYield, 0.06);
                    Func<BankTicker, double> performanceFn4 = (t) => 1 - t.DebtEquityRatio.DoubleReduce(1.5, 0);
                    //slope here is based on a graph where x-axis is MedianMonthPercVariance and y-axis is MedianMonthPerc
                    var minmax1 = new MinMaxStore<BankTicker>(performanceFn1);
                    var minmax2 = new MinMaxStore<BankTicker>(performanceFn2);
                    var minmax3 = new MinMaxStore<BankTicker>(performanceFn3);
                    var minmax4 = new MinMaxStore<BankTicker>(performanceFn4);
                    foreach (var ticker in tickers)
                    {
                        minmax1.Run(ticker);
                        minmax2.Run(ticker);
                        minmax3.Run(ticker);
                        minmax4.Run(ticker);
                    }
                    Func<BankTicker, double> performanceFnTotal = (t) => (performanceFn1(t).DoubleReduce(minmax1.Max, minmax1.Min) * 55) +
                                                                         (performanceFn2(t).DoubleReduce(minmax2.Max, minmax2.Min) * 25) +
                                                                         (performanceFn3(t).DoubleReduce(minmax3.Max, minmax3.Min) * 10) +
                                                                         (performanceFn4(t).DoubleReduce(minmax4.Max, minmax4.Min) * 10);
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
        }
        private double CalculatePriceChangeAvg(HistoryBar[] bars)
        {
            if(bars == null || bars.Length == 0) { return 0.0; }
            var now = DateTime.Now;
            var datePointer = _lookbackDate;
            var priceChanges = new List<double>();
            for (var i = 0; now.CompareTo(datePointer) > 0 && i < 1000; i++)
            {
                var from = datePointer;
                var to = datePointer.AddMonths(6);
                var priceWindow = bars.Where(b => b.BarDay.CompareTo(from) > 0 && b.BarDay.CompareTo(to) < 0).OrderByDescending(b => b.BarDayMilliseconds).ToArray();
                if (priceWindow.Length > 2 && priceWindow.Last().Price() > 0)
                {
                    priceChanges.Add((priceWindow.First().Price() - priceWindow.Last().Price()) * 100 / priceWindow.Last().Price());
                }
                datePointer = to;
            }
            if (priceChanges.Count > ((DataService.LOOKBACK_YEARS - 2) * 2))
            {
                var avg = priceChanges.Average();
                var stdev = Math.Sqrt(priceChanges.Sum(p => Math.Pow(p - avg, 2)) / priceChanges.Count);
                //var mode = priceChanges.OrderBy(p => p).Skip(priceChanges.Count / 2).FirstOrDefault();
                if (stdev > 0)
                {
                    return avg / stdev;
                }
                else
                {
                    _logger.LogWarning($"Standard deviation was zero for some reason. Symbol {bars[0].Symbol}");
                    return 0.0;
                }
            }
            else
            {
                _logger.LogDebug($"Insufficient price information for {bars[0].Symbol}");
                return 0.0;
            }
        }
    }
}
