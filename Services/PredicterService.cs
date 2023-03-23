using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NumbersGoUp.JsonModels;
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
        private readonly double _peratioCutoff;
        private readonly TickerService _tickerService;

        public double EncouragementMultiplier { get; }

        public PredicterService(IAppCancellation appCancellation, ILogger<PredicterService> logger, IBrokerService brokerService, TickerService tickerService, IStocksContextFactory contextFactory, IConfiguration configuration)
        {
            _logger = logger;
            _appCancellation = appCancellation;
            _brokerService = brokerService;
            _contextFactory = contextFactory;
            EncouragementMultiplier = Math.Min(Math.Max(double.TryParse(configuration["EncouragementMultiplier"], out var encouragementMultiplier) ? encouragementMultiplier : 0, -1), 1);
            _peratioCutoff = tickerService.PERatioCutoff;
            _tickerService = tickerService;
        }
        public async Task<Prediction> Predict(string symbol)
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
                        _logger.LogCritical($"Ticker {symbol} not found. Manual intervention required");
                        return null;
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
                    return null;
                }
                var lastMarketDay = await _brokerService.GetLastMarketDay();
                if (barMetrics[0].BarDay.CompareTo(lastMarketDay.Date.AddDays(-1)) < 0)
                {
                    _logger.LogError($"BarMetrics data for {symbol} isn't up to date! Returning default prediction.");
                    return null;
                }
                return new Prediction
                {
                    BuyMultiplier = Predict(ticker, barMetrics, true),
                    SellMultiplier = Predict(ticker, barMetrics, false),
                    Day = barMetrics[0].BarDay
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {symbol}");
            }
            return null;
        }
        public async Task<double> SectorPredict(string sector)
        {
            SectorMetric[] sectorMetrics;
            try
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    sectorMetrics = await stocksContext.SectorMetrics.Where(p => p.Sector == sector).OrderByDescending(b => b.BarDayMilliseconds).Take(FEATURE_HISTORY_DAY).ToArrayAsync(_appCancellation.Token);
                }
                if (sectorMetrics.Length != FEATURE_HISTORY_DAY)
                {
                    _logger.LogError($"SectorMetrics for {sector} did not return the required history (retrieved {sectorMetrics.Length} results). returning default prediction");
                    if (sectorMetrics.Length == 0)
                    {
                        _logger.LogError($"SectorMetrics for {sector} did not return any history. Assume sector is no longer valid.");
                        return 0.5;
                    }
                    return 0.5;
                }
                var lastMarketDay = await _brokerService.GetLastMarketDay();
                if (sectorMetrics[0].BarDay.CompareTo(lastMarketDay.Date.AddDays(-1)) < 0)
                {
                    _logger.LogError($"SectorMetrics data for {sector} isn't up to date! Returning default prediction.");
                    return 0.5;
                }
                return Predict(sectorMetrics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {sector}");
            }
            return 0.5;
        }
        public async Task<Prediction> Predict(Ticker ticker, DateTime day)
        {
            var symbol = ticker.Symbol;
            BarMetric[] barMetrics;
            var cutoff = new DateTimeOffset(day.Date).ToUnixTimeMilliseconds();
            try
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    barMetrics = await stocksContext.BarMetrics.Where(p => p.Symbol == symbol && p.BarDayMilliseconds <= cutoff)
                                        .OrderByDescending(b => b.BarDayMilliseconds).Take(FEATURE_HISTORY_DAY).Include(b => b.HistoryBar).ToArrayAsync(_appCancellation.Token);
                }
                return new Prediction
                {
                    BuyMultiplier = Predict(ticker, barMetrics, true),
                    SellMultiplier = Predict(ticker, barMetrics, false),
                    Day = barMetrics[0].BarDay
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {symbol}");
            }
            return null;
        }
        public async Task<double> SectorPredict(string sector, DateTime day)
        {
            SectorMetric[] sectorMetrics;
            var cutoff = new DateTimeOffset(day.Date).ToUnixTimeMilliseconds();
            try
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    sectorMetrics = await stocksContext.SectorMetrics.Where(p => p.Sector == sector && p.BarDayMilliseconds <= cutoff)
                                        .OrderByDescending(b => b.BarDayMilliseconds).Take(FEATURE_HISTORY_DAY).ToArrayAsync(_appCancellation.Token);
                }
                return Predict(sectorMetrics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {sector}");
            }
            return 0.5;
        }
        private double Predict(Ticker ticker, BarMetric[] barMetrics, bool buy)
        {
            if (barMetrics.Length == FEATURE_HISTORY_DAY)
            {
                const double sellCutoff = 50 / 3;
                double peRatio = ticker.EPS > 0 ? (barMetrics[0].HistoryBar.Price() / ticker.EPS) : _peratioCutoff;
                if (_tickerService.TickerWhitelist.TickerAny(ticker))
                {
                    peRatio = Math.Min(peRatio, Math.Min(12, _peratioCutoff / 2));
                }
                double pricePrediction;

                if (buy)
                {
                    pricePrediction = ((
                                        (barMetrics[0].AlmaSMA1.ZeroReduce(ticker.AlmaSma1Avg + ticker.AlmaSma1StDev, ticker.AlmaSma1Avg - ticker.AlmaSma1StDev) * 0.2) +
                                        (barMetrics[0].AlmaSMA2.ZeroReduce(ticker.AlmaSma2Avg + ticker.AlmaSma2StDev, ticker.AlmaSma2Avg - ticker.AlmaSma2StDev) * 0.2) +
                                        (barMetrics[0].AlmaSMA3.ZeroReduce(ticker.AlmaSma3Avg + ticker.AlmaSma3StDev, ticker.AlmaSma3Avg - ticker.AlmaSma3StDev) * 0.2)
                                      ) * barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(0, -ticker.AlmaVelStDev)) +
                                      (barMetrics[0].SMASMA.DoubleReduce(ticker.SMASMAAvg, ticker.SMASMAAvg - ticker.SMASMAStDev) * 0.2) +
                                      (barMetrics.CalculateAvgVelocity(b => b.SMASMA).DoubleReduce(0, -ticker.SMAVelStDev) * 0.1) +
                                      (barMetrics[0].RegressionSlope.DoubleReduce(0.1, -0.1) * 0.1);

                    pricePrediction = pricePrediction.Curve1(3 - ticker.PerformanceVector.DoubleReduce(60, 0, 2, 1));
                    pricePrediction *= 1 - peRatio.DoubleReduce(_peratioCutoff, Math.Min(12, _peratioCutoff / 2));
                }
                else if (barMetrics[0].AlmaSMA3 > 0)
                {
                    pricePrediction = ((
                                      ((1 - barMetrics[0].AlmaSMA1.ZeroReduce(ticker.AlmaSma1Avg + (ticker.AlmaSma1StDev * 2), ticker.AlmaSma1Avg - ticker.AlmaSma1StDev)) * barMetrics[0].AlmaSMA1.DoubleReduce(ticker.AlmaSma1Avg - ticker.AlmaSma1StDev, ticker.AlmaSma1Avg - (ticker.AlmaSma1StDev * 2)) * 0.2) +
                                      ((1 - barMetrics[0].AlmaSMA2.ZeroReduce(ticker.AlmaSma2Avg + (ticker.AlmaSma2StDev * 2), ticker.AlmaSma2Avg - ticker.AlmaSma2StDev)) * barMetrics[0].AlmaSMA2.DoubleReduce(ticker.AlmaSma2Avg - ticker.AlmaSma2StDev, ticker.AlmaSma2Avg - (ticker.AlmaSma2StDev * 2)) * 0.2) +
                                      ((1 - barMetrics[0].AlmaSMA3.ZeroReduce(ticker.AlmaSma3Avg + (ticker.AlmaSma3StDev * 2), ticker.AlmaSma3Avg - ticker.AlmaSma3StDev)) * barMetrics[0].AlmaSMA3.DoubleReduce(ticker.AlmaSma3Avg - ticker.AlmaSma3StDev, ticker.AlmaSma3Avg - (ticker.AlmaSma3StDev * 2)) * 0.2)
                                      ) * (1 - barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(ticker.AlmaVelStDev, 0))) +
                                      (barMetrics[0].SMASMA.ZeroReduce(0.5 * ticker.SMASMAStDev, -0.25 * ticker.SMASMAStDev) * 0.2) +
                                      ((1 - barMetrics.CalculateAvgVelocity(b => b.SMASMA).DoubleReduce(ticker.SMAVelStDev, 0)) * 0.1) +
                                      ((1 - barMetrics[0].RegressionSlope.ZeroReduce(0.1, -0.1)) * 0.1);

                    pricePrediction = pricePrediction.Curve1(ticker.PerformanceVector.DoubleReduce(100, sellCutoff, 2, 0.5));
                    pricePrediction += (1 - pricePrediction) * peRatio.DoubleReduce(_peratioCutoff * 2, _peratioCutoff);
                }
                else
                {
                    pricePrediction = Math.Max(peRatio.DoubleReduce(_peratioCutoff * 2, _peratioCutoff),
                                                (1 - ticker.PerformanceVector.DoubleReduce(TickerService.PERFORMANCE_CUTOFF, -TickerService.PERFORMANCE_CUTOFF)).Curve2(1));
                }

                if (buy)
                {
                    pricePrediction *= EncouragementMultiplier.DoubleReduce(0, -1);
                    pricePrediction += (1 - pricePrediction) * EncouragementMultiplier.DoubleReduce(1, 0);
                }
                else
                {
                    pricePrediction *= 1 - EncouragementMultiplier.DoubleReduce(1, 0);
                    pricePrediction += (1 - pricePrediction) * (1 - EncouragementMultiplier.DoubleReduce(0, -1));
                }

                return pricePrediction;
            }
            return 0.0;
        }
        private double Predict(SectorMetric[] sectorMetrics)
        {
            if (sectorMetrics.Length == FEATURE_HISTORY_DAY)
            {
                return ((1 - sectorMetrics[0].SMASMA.DoubleReduce(40, 20)) * sectorMetrics[0].SMASMA.DoubleReduce(20, -10) * 0.4) +
                       (sectorMetrics.CalculateAvgVelocity(b => b.SMASMA).DoubleReduce(1, -1) * 0.3) + 
                       (sectorMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(2, -2) * 0.3);
            }
            return 0.5;
        }
    }
    public class Prediction
    {
        public double SellMultiplier { get; set; }
        public double BuyMultiplier { get; set; }
        public DateTime Day { get; set; }
    }
}