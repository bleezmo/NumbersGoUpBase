using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NumbersGoUp.Models;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NumbersGoUp.Services
{
    public class PredicterService
    {
        public const int FEATURE_HISTORY_DAY = 5;
        public const int LOOKAHEAD_DAYS = 1;
        public const int LOOKAHEAD_DAYS_LONG = 15;
        public const int FEATURE_HISTORY_INTRADAY = 10;
        private readonly ILogger<PredicterService> _logger;
        private readonly IAppCancellation _appCancellation;
        private readonly IBrokerService _brokerService;
        private readonly IStocksContextFactory _contextFactory;
        private readonly double _encouragementMultiplier;

        public PredicterService(IAppCancellation appCancellation, ILogger<PredicterService> logger, IBrokerService brokerService, IStocksContextFactory contextFactory, IConfiguration configuration)
        {
            _logger = logger;
            _appCancellation = appCancellation;
            _brokerService = brokerService;
            _contextFactory = contextFactory;
            _encouragementMultiplier = Math.Min(Math.Max(double.TryParse(configuration["EncouragementMultiplier"], out var encouragementMultiplier) ? encouragementMultiplier : 0, -1), 1);
            
        }
        public Task<double?> BuyPredict(string symbol) => Predict(symbol, true);
        public Task<double> BuyPredict(BarMetric barMetric) => Predict(barMetric, true);
        public Task<double?> SellPredict(string symbol) => Predict(symbol, false);
        public Task<double> SellPredict(BarMetric barMetric) => Predict(barMetric, false);
        private async Task<double?> Predict(string symbol, bool buy)
        {
            BarMetric[] barMetrics;
            Ticker ticker;
            try
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    ticker = await stocksContext.Tickers.Where(t => t.Symbol == symbol).FirstOrDefaultAsync(_appCancellation.Token);
                    if (ticker == null)
                    {
                        _logger.LogCritical($"Ticker {ticker.Symbol} not found. Manual intervention required");
                        return 0.0;
                    }
                    barMetrics = await stocksContext.BarMetrics.Where(p => p.Symbol == symbol).OrderByDescending(b => b.BarDayMilliseconds).Take(FEATURE_HISTORY_DAY).Include(b => b.HistoryBar).ToArrayAsync(_appCancellation.Token);
                }
                if (barMetrics.Length != FEATURE_HISTORY_DAY)
                {
                    _logger.LogError($"BarMetrics for {symbol} did not return the required history (retrieved {barMetrics.Length} results). returning default prediction");
                    if (barMetrics.Length == 0)
                    {
                        _logger.LogError($"BarMetrics for {symbol} did not return any history. Assume ticker is no longer valid.");
                        return null;
                    }
                    return 0.0;
                }
                var lastMarketDay = await _brokerService.GetLastMarketDay();
                if (barMetrics.First().BarDay.CompareTo(lastMarketDay.Date.AddDays(-1)) < 0)
                {
                    _logger.LogError($"BarMetrics data for {symbol} isn't up to date! Returning default prediction.");
                    return 0.0;
                }
                return Predict(ticker, barMetrics, buy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {symbol}");
            }
            return 0.0;
        }
        private async Task<double> Predict(BarMetric barMetric, bool buy)
        {
            var symbol = barMetric.Symbol;
            BarMetric[] barMetrics;
            Ticker ticker;
            try
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    ticker = await stocksContext.Tickers.Where(t => t.Symbol == symbol).FirstOrDefaultAsync(_appCancellation.Token);
                    barMetrics = await stocksContext.BarMetrics.Where(p => p.Symbol == barMetric.Symbol && p.BarDayMilliseconds <= barMetric.BarDayMilliseconds)
                                        .OrderByDescending(b => b.BarDayMilliseconds).Take(FEATURE_HISTORY_DAY).Include(b => b.HistoryBar).ToArrayAsync(_appCancellation.Token);
                }
                return Predict(ticker, barMetrics, buy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {barMetric.Symbol}");
            }
            return 0.0;
        }
        private double Predict(Ticker ticker, BarMetric[] barMetrics, bool buy)
        {
            if (barMetrics.Length == FEATURE_HISTORY_DAY)
            {
                double peRatio = ticker.EPS > 0 ? (barMetrics[0].HistoryBar.Price() / ticker.EPS) : TickerService.PERATIO_CUTOFF;
                const double alpha = 0.45;
                double pricePrediction; double longPricePrediction;
                if (buy)
                {
                    longPricePrediction = (barMetrics[0].SMASMA.ZeroReduce(Math.Min(ticker.SMASMAAvg + (ticker.SMASMAStDev * 1.5), 90), Math.Min(ticker.SMASMAAvg - ticker.SMASMAStDev, 0)) * 0.31) +
                                          ((1 - barMetrics[0].AlmaSMA3.DoubleReduce(Math.Max(ticker.AlmaSma3Avg, 0), Math.Min(ticker.AlmaSma3Avg - (ticker.AlmaSma3StDev * 1.5), 0))) * 0.23) +
                                          ((1 - barMetrics[0].AlmaSMA2.DoubleReduce(Math.Max(ticker.AlmaSma2Avg, 0), Math.Min(ticker.AlmaSma2Avg - (ticker.AlmaSma2StDev * 1.5), 0))) * 0.23) +
                                          ((1 - barMetrics[0].AlmaSMA1.DoubleReduce(Math.Max(ticker.AlmaSma1Avg, 0), Math.Min(ticker.AlmaSma1Avg - (ticker.AlmaSma1StDev * 1.5), 0))) * 0.23);
                    longPricePrediction *= ticker.PerformanceVector.DoubleReduce(50, 0);
                }
                else
                {
                    longPricePrediction = (barMetrics[0].SMASMA.ZeroReduce(Math.Min(ticker.SMASMAAvg + ticker.SMASMAStDev, 90), Math.Min(ticker.SMASMAAvg - (ticker.SMASMAStDev * 1.5), 0)) * 0.31) +
                                          (barMetrics[0].AlmaSMA3.DoubleReduce(Math.Min(ticker.AlmaSma3Avg + (ticker.AlmaSma3StDev * 1.5), 90), Math.Max(ticker.AlmaSma3Avg, 0)) * 0.23) +
                                          (barMetrics[0].AlmaSMA2.DoubleReduce(Math.Min(ticker.AlmaSma2Avg + (ticker.AlmaSma2StDev * 1.5), 90), Math.Max(ticker.AlmaSma2Avg, 0)) * 0.23) +
                                          (barMetrics[0].AlmaSMA1.DoubleReduce(Math.Min(ticker.AlmaSma1Avg + (ticker.AlmaSma1StDev * 1.5), 90), Math.Max(ticker.AlmaSma1Avg, 0)) * 0.23);
                    longPricePrediction *= 1 - ticker.PerformanceVector.DoubleReduce(150, 0);
                }
                const double exp = 2; const double radius = 0.2;
                if (buy)
                {
                    var profitLossMin = ticker.ProfitLossAvg - (ticker.ProfitLossStDev * 1.5);
                    pricePrediction = ((1 - (barMetrics[0].PriceSMA3.DoubleReduce(0, -90) * barMetrics[0].PriceSMA3.FibonacciReduce(0, -90, exp, radius))) * 0.17) +
                                          ((1 - (barMetrics[0].PriceSMA2.DoubleReduce(0, -90) * barMetrics[0].PriceSMA2.FibonacciReduce(0, -90, exp, radius))) * 0.17) +
                                          ((1 - (barMetrics[0].PriceSMA1.DoubleReduce(0, -90) * barMetrics[0].PriceSMA1.FibonacciReduce(0, -90, exp, radius))) * 0.17) +
                    ((1 - (barMetrics[0].ProfitLossPerc.DoubleReduce(ticker.ProfitLossAvg, profitLossMin) * barMetrics[0].ProfitLossPerc.FibonacciReduce(ticker.ProfitLossAvg, profitLossMin, exp, radius))) * 0.17) +
                    (barMetrics.Take(3).CalculateAcceleration(b => b.PriceSMA1).DoubleReduce(10, 0) * barMetrics[0].VolAlmaSMA.ZeroReduce(20, -20) * 0.16) +
                    ((1 - barMetrics.Take(3).CalculateAcceleration(b => b.StDevSMA1).DoubleReduce(2, -8)) * barMetrics[0].StDevSMA1.ZeroReduce(10, -10) * 0.16);
                    pricePrediction *= Math.Pow(barMetrics[0].ProfitLossPerc.DoubleReduce(profitLossMin, profitLossMin - 20), 2);
                }
                else
                {
                    var profitLossMax = ticker.ProfitLossAvg + (ticker.ProfitLossStDev * 1.5);
                    pricePrediction = (barMetrics[0].PriceSMA3.DoubleReduce(90, 0) * barMetrics[0].PriceSMA3.FibonacciReduce(90, 0, exp, radius) * 0.17) +
                                          (barMetrics[0].PriceSMA2.DoubleReduce(90, 0) * barMetrics[0].PriceSMA2.FibonacciReduce(90, 0, exp, radius) * 0.17) +
                                          (barMetrics[0].PriceSMA1.DoubleReduce(90, 0) * barMetrics[0].PriceSMA1.FibonacciReduce(90, 0, exp, radius) * 0.17) +
                    (barMetrics[0].ProfitLossPerc.DoubleReduce(profitLossMax, ticker.ProfitLossAvg) * barMetrics[0].ProfitLossPerc.FibonacciReduce(profitLossMax, ticker.ProfitLossAvg, exp, radius) * 0.17) +
                    ((1 - barMetrics.Take(3).CalculateAcceleration(b => b.PriceSMA1).DoubleReduce(0, -10)) * barMetrics[0].VolAlmaSMA.ZeroReduce(20, -20) * 0.16) +
                    ((1 - barMetrics.Take(3).CalculateAcceleration(b => b.StDevSMA1).DoubleReduce(2, -8)) * barMetrics[0].StDevSMA1.ZeroReduce(10, -10) * 0.16);
                }

                var totalPrediction = (pricePrediction * alpha) + (longPricePrediction * (1 - alpha));
                if (buy)
                {
                    totalPrediction *= (1 - peRatio.DoubleReduce(TickerService.PERATIO_CUTOFF, TickerService.PERATIO_CUTOFF * 0.5));
                }
                totalPrediction += buy ? ((1 - totalPrediction) * _encouragementMultiplier.DoubleReduce(1, 0)) : ((1 - totalPrediction) * (1 - _encouragementMultiplier.DoubleReduce(0, -1)));
                return totalPrediction;
            }
            return 0.0;
        }
    }
}
