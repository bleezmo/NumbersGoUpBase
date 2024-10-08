﻿using Microsoft.EntityFrameworkCore;
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
        public const int FEATURE_HISTORY_DAY = 7;
        private readonly ILogger<PredicterService> _logger;
        private readonly IAppCancellation _appCancellation;
        private readonly IBrokerService _brokerService;
        private readonly IStocksContextFactory _contextFactory;

        public double EncouragementMultiplier { get; }

        public PredicterService(IAppCancellation appCancellation, ILogger<PredicterService> logger, IBrokerService brokerService, 
                                IStocksContextFactory contextFactory, IConfiguration configuration)
        {
            _logger = logger;
            _appCancellation = appCancellation;
            _brokerService = brokerService;
            _contextFactory = contextFactory;
            EncouragementMultiplier = Math.Min(Math.Max(double.TryParse(configuration["EncouragementMultiplier"], out var encouragementMultiplier) ? encouragementMultiplier : 0, -1), 1);
        }
        public async Task<Prediction> Predict(Ticker ticker, DateTime? day = null)
        {
            var symbol = ticker.Symbol;
            BarMetric[] barMetrics;
            try
            {
                if (!IsValidTicker(ticker))
                {
                    if (!day.HasValue && DateTime.Now.DayOfWeek != TickerService.RUN_LOAD)
                    {
                        _logger.LogWarning($"Ticker {ticker.Symbol} has invalid metric averages");
                    }
                    return null;
                }
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    IQueryable<BarMetric> barMetricQuery = null;
                    if (day.HasValue)
                    {
                        var cutoff = new DateTimeOffset(day.Value.Date).ToUnixTimeMilliseconds();
                        barMetricQuery = stocksContext.BarMetrics.Where(p => p.Symbol == symbol && p.BarDayMilliseconds <= cutoff);
                    }
                    else
                    {
                        barMetricQuery = stocksContext.BarMetrics.Where(p => p.Symbol == symbol);
                    }
                    barMetrics = await barMetricQuery.OrderByDescending(b => b.BarDayMilliseconds).Take(FEATURE_HISTORY_DAY)
                                                     .Include(b => b.HistoryBar).ToArrayAsync(_appCancellation.Token);
                }
                if (barMetrics.Length != FEATURE_HISTORY_DAY)
                {
#if !DEBUG
                    _logger.LogError($"BarMetrics for {symbol} did not return the required history (retrieved {barMetrics.Length} results). returning default prediction");
#endif
                    if (barMetrics.Length == 0)
                    {
#if !DEBUG
                        _logger.LogError($"BarMetrics for {symbol} did not return any history. Assume ticker is no longer valid.");
#endif
                        return null;
                    }
                    return null;
                }
                DateTime checkDay = day.HasValue ? day.Value.AddDays(-7) : (await _brokerService.GetLastMarketDay()).Date.AddDays(-1);
                if (barMetrics[0].BarDay.CompareTo(checkDay) < 0)
                {
#if !DEBUG
                    _logger.LogError($"BarMetrics data for {symbol} isn't up to date! Returning default prediction.");
#endif
                    return null;
                }
                return new Prediction
                {
                    BuyMultiplier = Math.Round(Predict(ticker, barMetrics, true), 3, MidpointRounding.AwayFromZero),
                    SellMultiplier = Math.Round(Predict(ticker, barMetrics, false), 3, MidpointRounding.AwayFromZero),
                    RecentBarMetric = barMetrics[0]
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving metrics for {symbol}");
            }
            return null;
        }
        public double Predict(Ticker ticker, BarMetric[] barMetrics, bool buy)
        {
            if (barMetrics.Length == FEATURE_HISTORY_DAY)
            {
                double pricePrediction;

                if (buy)
                {
                    var bullPricePrediction = ((
                                        ((1 - barMetrics[0].AlmaSMA1.ZeroReduce(ticker.AlmaSma1Avg + ticker.AlmaSma1StDev, ticker.AlmaSma1Avg - ticker.AlmaSma1StDev)) * 0.2) +
                                        ((1 - barMetrics[0].AlmaSMA2.ZeroReduce(ticker.AlmaSma2Avg + ticker.AlmaSma2StDev, ticker.AlmaSma2Avg - ticker.AlmaSma2StDev)) * 0.2) +
                                        ((1 - barMetrics[0].AlmaSMA3.ZeroReduce(ticker.AlmaSma3Avg + ticker.AlmaSma3StDev, ticker.AlmaSma3Avg - ticker.AlmaSma3StDev)) * 0.2)
                                      ) * barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(0.5 * ticker.AlmaVelStDev, -ticker.AlmaVelStDev) * barMetrics.CalculateAvgAcceleration(b => b.AlmaSMA3).DoubleReduce(0, -ticker.AlmaVelStDev)) +
                                      ((1 - barMetrics[0].SMASMA.DoubleReduce(ticker.SMASMAAvg, ticker.SMASMAAvg - ticker.SMASMAStDev)) *
                                       barMetrics.CalculateAvgVelocity(b => b.SMASMA).ZeroReduce(ticker.SMAVelStDev, -ticker.SMAVelStDev) *
                                       barMetrics.CalculateAvgAcceleration(b => b.SMASMA).DoubleReduce(0, -ticker.SMAVelStDev) * 0.25) +
                                      (barMetrics[0].WeekTrend.DoubleReduce(ticker.WeekTrendAvg, ticker.WeekTrendAvg - ticker.WeekTrendStDev) *
                                       barMetrics.CalculateAvgVelocity(b => b.WeekTrend).DoubleReduce(0.5 * ticker.WeekTrendVelStDev, -ticker.WeekTrendVelStDev) * 0.15);

                    var bearPricePrediction = ((
                                        ((1 - barMetrics[0].AlmaSMA1.DoubleReduce(ticker.AlmaSma1Avg, ticker.AlmaSma1Avg - (ticker.AlmaSma1StDev * 1.5))) * 0.2) +
                                        ((1 - barMetrics[0].AlmaSMA2.DoubleReduce(ticker.AlmaSma2Avg, ticker.AlmaSma2Avg - (ticker.AlmaSma2StDev * 1.5))) * 0.2) +
                                        ((1 - barMetrics[0].AlmaSMA3.DoubleReduce(ticker.AlmaSma3Avg, ticker.AlmaSma3Avg - (ticker.AlmaSma3StDev * 1.5))) * 0.2)
                                      ) * barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(0.5 * ticker.AlmaVelStDev, -ticker.AlmaVelStDev) * barMetrics.CalculateAvgAcceleration(b => b.AlmaSMA3).DoubleReduce(0.25 * ticker.AlmaVelStDev, -ticker.AlmaVelStDev)) +
                                      ((1 - barMetrics[0].SMASMA.DoubleReduce(ticker.SMASMAAvg, ticker.SMASMAAvg - (ticker.SMASMAStDev * 1.5))) *
                                       barMetrics.CalculateAvgVelocity(b => b.SMASMA).ZeroReduce(0.5 * ticker.SMAVelStDev, -0.5 * ticker.SMAVelStDev) *
                                       barMetrics.CalculateAvgAcceleration(b => b.SMASMA).DoubleReduce(0.25 * ticker.SMAVelStDev, -ticker.SMAVelStDev) * 0.25) +
                                      (barMetrics[0].WeekTrend.DoubleReduce(ticker.WeekTrendAvg + ticker.WeekTrendStDev, ticker.WeekTrendAvg - ticker.WeekTrendStDev) *
                                       barMetrics.CalculateAvgVelocity(b => b.WeekTrend).DoubleReduce(ticker.WeekTrendVelStDev, -ticker.WeekTrendVelStDev) * 0.15);

                    var coeff = barMetrics.Average(b => b.SMASMA).DoubleReduce(ticker.SMASMAAvg, ticker.SMASMAAvg - ticker.SMASMAStDev) * barMetrics.CalculateAvgVelocity(b => b.SMASMA).DoubleReduce(0.5 * ticker.SMAVelStDev, -ticker.SMAVelStDev);
                    pricePrediction = (coeff * bullPricePrediction) + ((1 - coeff) * bearPricePrediction);
                    pricePrediction *= (2 - ticker.SMASMAAvg.DoubleReduce(20, -20) - (ticker.ProfitLossAvg / ticker.ProfitLossStDev).DoubleReduce()) / 2;
                }
                else
                {
                    pricePrediction = ((
                                        (barMetrics[0].AlmaSMA1.ZeroReduce(ticker.AlmaSma1Avg + ticker.AlmaSma1StDev, ticker.AlmaSma1Avg - ticker.AlmaSma1StDev) * 0.2) +
                                        (barMetrics[0].AlmaSMA2.ZeroReduce(ticker.AlmaSma2Avg + ticker.AlmaSma2StDev, ticker.AlmaSma2Avg - ticker.AlmaSma2StDev) * 0.2) +
                                        (barMetrics[0].AlmaSMA3.ZeroReduce(ticker.AlmaSma3Avg + ticker.AlmaSma3StDev, ticker.AlmaSma3Avg - ticker.AlmaSma3StDev) * 0.2)
                                       ) * (1 - barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(ticker.AlmaVelStDev, -ticker.AlmaVelStDev)) * (1 - barMetrics.CalculateAvgAcceleration(b => b.AlmaSMA3).DoubleReduce(ticker.AlmaVelStDev, -ticker.AlmaVelStDev))
                                      ) +
                                      (barMetrics[0].SMASMA.DoubleReduce(ticker.SMASMAAvg + (ticker.SMASMAStDev * 1.5), ticker.SMASMAAvg) *
                                        barMetrics.CalculateAvgVelocity(b => b.SMASMA).ZeroReduce(ticker.SMAVelStDev, -ticker.SMAVelStDev) *
                                        (1 - barMetrics.CalculateAvgAcceleration(b => b.SMASMA).DoubleReduce(ticker.SMAVelStDev, 0)) * 0.2) +
                                    ((1 - barMetrics[0].WeekTrend.DoubleReduce(ticker.WeekTrendAvg, ticker.WeekTrendAvg - ticker.WeekTrendStDev)) *
                                        (1 - barMetrics.CalculateAvgVelocity(b => b.WeekTrend).DoubleReduce(0, -ticker.WeekTrendVelStDev)) * 0.2);
                    pricePrediction *= (ticker.SMASMAAvg.DoubleReduce(20, -20) + (ticker.ProfitLossAvg / ticker.ProfitLossStDev).DoubleReduce()) / 2;
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
        private bool IsValidTicker(Ticker ticker) =>
            ticker.AlmaSma1StDev > 0 && ticker.AlmaSma2StDev > 0 && ticker.AlmaSma3StDev > 0 && ticker.AlmaVelStDev > 0 &&
            ticker.SMASMAStDev > 0 && ticker.SMAVelStDev > 0;

    }
    public class Prediction
    {
        public double SellMultiplier { get; set; }
        public double BuyMultiplier { get; set; }
        public BarMetric RecentBarMetric { get; set; }
    }
}