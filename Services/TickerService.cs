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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NumbersGoUp.Services
{
    public class TickerService
    {
        public const int PERFORMANCE_CUTOFF = 20;
        public const int PERFORMANCE_AVGS_LOOKBACK = 375;

        public const DayOfWeek RUN_AVGS = DayOfWeek.Wednesday;
        public const DayOfWeek RUN_LOAD = DayOfWeek.Tuesday;

        private const double PICK_WEIGHT = 0.5;

        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TickerService> _logger;
        private readonly IBrokerService _brokerService;
        private readonly IStocksContextFactory _contextFactory;
        private readonly IRuntimeSettings _runtimeSettings;
        private readonly ITickerPickProcessor _tickerPickProcessor;

        public TickerService(IStocksContextFactory contextFactory, IRuntimeSettings runtimeSettings, IAppCancellation appCancellation, 
                                ILogger<TickerService> logger, IBrokerService brokerService, ITickerPickProcessor tickerPickProcessor)
        {
            _appCancellation = appCancellation;
            _logger = logger;
            _brokerService = brokerService;
            _contextFactory = contextFactory;
            _runtimeSettings = runtimeSettings;
            _tickerPickProcessor = tickerPickProcessor;
        }
        public async Task<IEnumerable<Ticker>> GetTickers()
        {
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var tickers = await stocksContext.Tickers.Where(t => t.PerformanceVector > PERFORMANCE_CUTOFF)
                                                  .OrderByDescending(t => t.PerformanceVector).ToListAsync(_appCancellation.Token);
                return tickers;
            }
        }
        public async Task<IEnumerable<Ticker>> GetTickers(string[] symbols)
        {
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var tickers = await stocksContext.Tickers.Where(t => symbols.Contains(t.Symbol)).ToListAsync(_appCancellation.Token);
                return tickers;
            }
        }
        public async Task<IEnumerable<Ticker>> GetFullTickerList()
        {
            using(var stocksContext = _contextFactory.CreateDbContext())
            {
                var tickers = await stocksContext.Tickers.ToListAsync(_appCancellation.Token);
                return tickers;
            }
        }
        public async Task LoadTickers()
        {
            try
            {
                await Load();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "error occurred when loading ticker data");
                throw;
            }
        }

        public async Task ApplyAverages()
        {
            var now = DateTimeOffset.UtcNow;
            if (_runtimeSettings.ForceDataCollection || now.DayOfWeek == RUN_AVGS) //don't need to check every day
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var nowMillis = now.ToUnixTimeMilliseconds();
                    var tickers = await stocksContext.Tickers.ToListAsync(_appCancellation.Token);
                    //var nowTestMillis = new DateTimeOffset(now.AddYears(-5)).ToUnixTimeMilliseconds();
                    foreach (var ticker in tickers)
                    {
                        var bars = await stocksContext.BarMetrics.Where(b => b.Symbol == ticker.Symbol).OrderByDescending(b => b.BarDayMilliseconds).Take(PERFORMANCE_AVGS_LOOKBACK).ToArrayAsync(_appCancellation.Token);
                        if (!bars.Any())
                        {
                            _logger.LogError($"No bar metric history found for {ticker.Symbol}");
                            continue;
                        }
                        if (bars.Length < PERFORMANCE_AVGS_LOOKBACK)
                        {
                            _logger.LogError($"Insufficient bar metric history for {ticker.Symbol}");
                            continue;
                        }
                        ticker.SMASMAAvg = bars.Average(b => b.SMASMA);
                        ticker.SMASMAStDev = Math.Sqrt(bars.Sum(b => Math.Pow(b.SMASMA - ticker.SMASMAAvg, 2)) / bars.Length);

                        ticker.AlmaSma1Avg = bars.Average(b => b.AlmaSMA1);
                        ticker.AlmaSma1StDev = Math.Sqrt(bars.Sum(b => Math.Pow(b.AlmaSMA1 - ticker.AlmaSma1Avg, 2)) / bars.Length);

                        ticker.AlmaSma2Avg = bars.Average(b => b.AlmaSMA2);
                        ticker.AlmaSma2StDev = Math.Sqrt(bars.Sum(b => Math.Pow(b.AlmaSMA2 - ticker.AlmaSma2Avg, 2)) / bars.Length);

                        ticker.AlmaSma3Avg = bars.Average(b => b.AlmaSMA3);
                        ticker.AlmaSma3StDev = Math.Sqrt(bars.Sum(b => Math.Pow(b.AlmaSMA3 - ticker.AlmaSma3Avg, 2)) / bars.Length);

                        ticker.ProfitLossAvg = bars.Average(b => b.ProfitLossPerc);
                        ticker.ProfitLossStDev = Math.Sqrt(bars.Sum(b => Math.Pow(b.ProfitLossPerc - ticker.ProfitLossAvg, 2)) / bars.Length);

                        ticker.AlmaVelStDev = (bars.CalculateVelocityStDev(b => b.AlmaSMA1) + bars.CalculateVelocityStDev(b => b.AlmaSMA2) + bars.CalculateVelocityStDev(b => b.AlmaSMA3)) / 3;
                        ticker.SMAVelStDev = bars.CalculateVelocityStDev(b => b.SMASMA);

                        (ticker.WeekTrendAvg, ticker.WeekTrendStDev) = bars.CalculateAvgStDev(b => b.WeekTrend);
                        ticker.WeekTrendVelStDev = bars.CalculateVelocityStDev(b => b.WeekTrend);

                        var maxMonthConsecutiveLosses = 0.0;
                        var consecutiveLosses = 0;
                        const int monthLength = 20;
                        for (var i = 0; i < bars.Length; i += monthLength)
                        {
                            consecutiveLosses = bars.Skip(i).Take(monthLength).Average(b => b.SMASMA) > 0 ? 0 : (consecutiveLosses + 1);
                            maxMonthConsecutiveLosses = maxMonthConsecutiveLosses < consecutiveLosses ? consecutiveLosses : maxMonthConsecutiveLosses;
                        }
                        ticker.MaxMonthConsecutiveLosses = maxMonthConsecutiveLosses;

                        ticker.LastCalculatedAvgs = now.UtcDateTime;
                        ticker.LastCalculatedAvgsMillis = nowMillis;
                        stocksContext.Tickers.Update(ticker);
                    }
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                }
            }
        }
        public async Task Load()
        {
            var now = DateTimeOffset.UtcNow;
            if (_runtimeSettings.ForceDataCollection || now.DayOfWeek == RUN_LOAD) //don't need to check every day
            {
                await _brokerService.Ready();
                var nowMillis = now.ToUnixTimeMilliseconds();
                var positions = await _brokerService.GetPositions();
                var tickerPicks = await _tickerPickProcessor.GetTickers();
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var tickers = await stocksContext.Tickers.ToArrayAsync(_appCancellation.Token);
                    var bankTickers = await stocksContext.TickerBank.ToArrayAsync(_appCancellation.Token);
                    var count = tickers.Length;
                    foreach (var tickerPick in tickerPicks)
                    {
                        var bankTicker = bankTickers.FirstOrDefault(t => t.Symbol == tickerPick.Symbol);
                        var ticker = tickers.FirstOrDefault(t => t.Symbol == tickerPick.Symbol);
                        if(ticker != null && bankTicker != null)
                        {
                            TickerCopy(ticker, bankTicker, tickerPick);
                            ticker.LastCalculated = now.UtcDateTime;
                            ticker.LastCalculatedMillis = nowMillis;
                            stocksContext.Tickers.Update(ticker);
                        }
                        else if(ticker == null && bankTicker != null)
                        {
                            if(tickerPick.Score > 0)
                            {
                                stocksContext.Tickers.Add(TickerCopy(new Ticker
                                {
                                    Symbol = bankTicker.Symbol,
                                    LastCalculated = now.UtcDateTime,
                                    LastCalculatedMillis = nowMillis
                                }, bankTicker, tickerPick));
                            }
                        }
                        else if(ticker != null && bankTicker == null)
                        {
                            ticker.PerformanceVector = ((1 - PICK_WEIGHT) * Math.Max(ticker.PerformanceVector - 5, 0)) + (PICK_WEIGHT * tickerPick.Score);
                            ticker.LastCalculated = now.UtcDateTime;
                            ticker.LastCalculatedMillis = nowMillis;
                            stocksContext.Tickers.Update(ticker);
                        }
                        else if (positions.Any(p => p.Symbol == tickerPick.Symbol))
                        {
                            _logger.LogError($"Invalid state for ticker pick {tickerPick.Symbol}");
                        }
                    }
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    foreach(var ticker in tickers)
                    {
                        var tickerPick = tickerPicks.FirstOrDefault(t => t.Symbol == ticker.Symbol && t.Score > 0);
                        if(tickerPick == null)
                        {
                            var hasPosition = positions.Any(p => p.Symbol == ticker.Symbol);
                            var bankTicker = bankTickers.FirstOrDefault(t => t.Symbol == ticker.Symbol);
                            if(bankTicker == null && hasPosition)
                            {
                                ticker.PerformanceVector = Math.Max(ticker.PerformanceVector - 5, 0);
                                ticker.LastCalculated = now.UtcDateTime;
                                ticker.LastCalculatedMillis = nowMillis;
                                stocksContext.Tickers.Update(ticker);
                            }
                            else if (bankTicker != null && hasPosition)
                            {
                                var pv = ticker.PerformanceVector;
                                TickerCopy(ticker, bankTicker);
                                ticker.PerformanceVector = Math.Max(pv - 5, 0);
                                ticker.LastCalculated = now.UtcDateTime;
                                ticker.LastCalculatedMillis = nowMillis;
                                stocksContext.Tickers.Update(ticker);
                            }
                            else if (!hasPosition)
                            {
                                stocksContext.Tickers.Remove(ticker);
                            }
                        }
                    }
                    await stocksContext.SaveChangesAsync(_appCancellation.Token);
                }
            }
        }
        private static void TickerCopy(Ticker ticker, BankTicker bankTicker)
        {
            ticker.Sector = bankTicker.Sector;
            ticker.DividendYield = bankTicker.DividendYield;
            ticker.Earnings = bankTicker.Earnings;
            ticker.PerformanceVector = 0;
        }
        private static Ticker TickerCopy(Ticker ticker, BankTicker bankTicker, TickerPick tickerPick)
        {
            TickerCopy(ticker, bankTicker);
            ticker.PerformanceVector = (PICK_WEIGHT * tickerPick.Score) + ((1 - PICK_WEIGHT) * bankTicker.PerformanceVector);
            return ticker;
        }
    }
}
