using CsvHelper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NumbersGoUp.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Utils
{
    public interface ITickerBankProcessor
    {
        Task<TickerBankProcessorResult> DownloadTickers(bool alwaysProcessData = false);
        void UpdateBankTicker(BankTicker src, BankTicker dest);
    }

    public class TradingViewTickerBankProcessor : ITickerBankProcessor
    {
        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TradingViewTickerBankProcessor> _logger;
        private readonly ITickerHash _tickerHash;
        private readonly ITickerFile _tickerFile;

        public TradingViewTickerBankProcessor(IAppCancellation appCancellation, ILogger<TradingViewTickerBankProcessor> logger, ITickerHash tickerHash, ITickerFile tickerFile)
        {
            _appCancellation = appCancellation;
            _logger = logger;
            _tickerHash = tickerHash;
            _tickerFile = tickerFile;
        }
        public async Task<TickerBankProcessorResult> DownloadTickers(bool alwaysProcessData = false)
        {
            var oldHash = await _tickerHash.GetCurrentHash();
            var currentHash = string.Empty;
            using (var md5 = MD5.Create())
            {
                using (var stream = await _tickerFile.OpenRead())
                {
                    currentHash = Convert.ToBase64String(md5.ComputeHash(stream));
                }
            }
            List<ProcessorBankTicker> tickers = new List<ProcessorBankTicker>();
            if (oldHash != currentHash || alwaysProcessData)
            {
                using (var sr = new StreamReader(await _tickerFile.OpenRead()))
                {
                    /*
                     * Ticker,Description,Volume,Market Capitalization,Price to Earnings Ratio (TTM),Sector,Current Ratio (MRQ),
                     * Debt to Equity Ratio (MRQ),Dividend Yield Forward,EBITDA (TTM),Enterprise Value/EBITDA (TTM),EPS Diluted (TTM)
                     */
                    int? tickerIndex = null, sectorIndex = null, marketCapIndex = null, peRatioIndex = null, currentRatioIndex = null, debtEquityRatioIndex = null, 
                         dividendIndex = null, ebitdaIndex = null, evebitdaIndex = null, epsFYIndex = null, epsIndex = null, priceIndex = null, sharesIndex = null, evIndex = null, 
                         currentEPSIndex = null, futureEPSIndex = null, epsQoQIndex = null, epsGrowthIndex = null, recentEarningsIndex = null, revenueGrowthIndex = null, 
                         incomeIndex = null, countryIndex = null;
                    using (var csv = new CsvReader(sr, CultureInfo.InvariantCulture))
                    {
                        string[] headers = null;
                        for (var dc = 0; await csv.ReadAsync() && dc < 5000; dc++)
                        {
                            if (dc == 0)
                            {
                                csv.ReadHeader();
                                headers = csv.HeaderRecord;
                                for (var i = 0; i < headers.Length; i++)
                                {
                                    if (headers[i] == "Ticker") { tickerIndex = i; }
                                    if (headers[i] == "Sector") { sectorIndex = i; }
                                    if (headers[i] == "Market Capitalization") { marketCapIndex = i; }
                                    if (headers[i] == "Price to Earnings Ratio (TTM)") { peRatioIndex = i; }
                                    if (headers[i] == "Current Ratio (MRQ)") { currentRatioIndex = i; }
                                    if (headers[i] == "Debt to Equity Ratio (MRQ)") { debtEquityRatioIndex = i; }
                                    if (headers[i] == "Dividend Yield Forward") { dividendIndex = i; }
                                    if (headers[i] == "EBITDA (TTM)") { ebitdaIndex = i; }
                                    if (headers[i] == "Enterprise Value/EBITDA (TTM)") { evebitdaIndex = i; }
                                    if (headers[i] == "EPS Diluted (FY)") { epsFYIndex = i; }
                                    if (headers[i] == "EPS Diluted (TTM)") { epsIndex = i; }
                                    if (headers[i] == "Price") { priceIndex = i; }
                                    if (headers[i] == "EPS Diluted (MRQ)") { currentEPSIndex = i; }
                                    if (headers[i] == "EPS Forecast (MRQ)") { futureEPSIndex = i; }
                                    if (headers[i] == "Total Shares Outstanding") { sharesIndex = i; }
                                    if (headers[i] == "Enterprise Value (MRQ)") { evIndex = i; }
                                    if (headers[i] == "EPS Diluted (Quarterly QoQ Growth)") { epsQoQIndex = i; }
                                    if (headers[i] == "EPS Diluted (TTM YoY Growth)") { epsGrowthIndex = i; }
                                    if (headers[i] == "Recent Earnings Date") { recentEarningsIndex = i; }
                                    if (headers[i] == "Revenue (TTM YoY Growth)") { revenueGrowthIndex = i; }
                                    if (headers[i] == "Net Income (FY)") { incomeIndex = i; }
                                    if (headers[i] == "Country") { countryIndex = i; }
                                }
                            }
                            else
                            {
                                var ticker = new ProcessorBankTicker();
                                if (tickerIndex.HasValue)
                                {
                                    ticker.Ticker.Symbol = csv[tickerIndex.Value];
                                    if (ticker.Ticker.Symbol.Contains('.'))
                                    {
                                        _logger.LogInformation($"Excluding {ticker.Ticker.Symbol}");
                                        continue;
                                    }
                                }
                                else
                                {
                                    _logger.LogError("No ticker symbol found!!");
                                    continue;
                                }
                                if (sectorIndex.HasValue)
                                {
                                    ticker.Ticker.Sector = csv[sectorIndex.Value];
                                }
                                else
                                {
                                    //_logger.LogError($"Sector not found for {ticker.Ticker.Symbol}");
                                }
                                if (countryIndex.HasValue)
                                {
                                    ticker.Ticker.Country = csv[countryIndex.Value];
                                }
                                if(revenueGrowthIndex.HasValue && double.TryParse(csv[revenueGrowthIndex.Value] ,out var revenueGrowth))
                                {
                                    ticker.RevenueGrowth = revenueGrowth;
                                }
                                if(incomeIndex.HasValue && double.TryParse(csv[incomeIndex.Value], out var income))
                                {
                                    ticker.Income = income;
                                }
                                if (peRatioIndex.HasValue && double.TryParse(csv[peRatioIndex.Value], out var peRatio))
                                {
                                    ticker.Ticker.PERatio = peRatio;
                                }
                                else
                                {
                                    //_logger.LogWarning($"PE Ratio not found for {ticker.Ticker.Symbol}");
                                }
                                if (currentRatioIndex.HasValue && double.TryParse(csv[currentRatioIndex.Value], out var currentRatio))
                                {
                                    ticker.Ticker.CurrentRatio = currentRatio;
                                }
                                else
                                {
                                    //_logger.LogWarning($"Current Ratio not found for {ticker.Symbol}");
                                }
                                if (debtEquityRatioIndex.HasValue && double.TryParse(csv[debtEquityRatioIndex.Value], out var debtEquityRatio))
                                {
                                    ticker.Ticker.DebtEquityRatio = debtEquityRatio;
                                }
                                else
                                {
                                    //_logger.LogWarning($"Debt Equity Ratio not found for {ticker.Ticker.Symbol}");
                                }
                                if (dividendIndex.HasValue && double.TryParse(csv[dividendIndex.Value], out var dividend))
                                {
                                    ticker.Ticker.DividendYield = dividend / 100;
                                }
                                else
                                {
                                    _logger.LogWarning($"Dividend not found for {ticker.Ticker.Symbol}");
                                }
                                if(epsIndex.HasValue && double.TryParse(csv[epsIndex.Value], out var eps))
                                {
                                    if (currentEPSIndex.HasValue && double.TryParse(csv[currentEPSIndex.Value], out var currentEPS) &&
                                        futureEPSIndex.HasValue && double.TryParse(csv[futureEPSIndex.Value], out var futureEPS) &&
                                        epsQoQIndex.HasValue && double.TryParse(csv[epsQoQIndex.Value], out var epsQoQGrowth) &&
                                        epsQoQGrowth > -100 && epsQoQGrowth < 100)
                                    {
                                        ticker.QoQEPSGrowth = epsQoQGrowth;
                                        epsQoQGrowth = (epsQoQGrowth + 100) / 100;
                                        var pastEPS = currentEPS / epsQoQGrowth;
                                        var quarters = new[] { pastEPS, currentEPS, futureEPS };
                                        var finalQ = quarters.CalculateFutureRegression(1);
                                        var calculatedEPS = pastEPS + currentEPS + futureEPS + finalQ;
                                        if (finalQ > 0 && finalQ > quarters.Min() && eps > 0)
                                        {
                                            var coeff = (calculatedEPS / eps).DoubleReduce(2, 1);
                                            ticker.Ticker.EPS = (coeff * eps) + ((1 - coeff) * calculatedEPS);
                                        }
                                        else
                                        {
                                            ticker.Ticker.EPS = Math.Min(eps, calculatedEPS * 0.7);
                                        }
                                    }
                                    else if (epsGrowthIndex.HasValue && double.TryParse(csv[epsGrowthIndex.Value], out var epsGrowth) && 
                                             epsGrowth > -100 && epsGrowth < 300)
                                    {
                                        ticker.YoYEPSGrowth = epsGrowth;
                                        epsGrowth = (epsGrowth + 100) / 100;
                                        if (epsGrowth < 1)
                                        {
                                            var coeff = epsGrowth;
                                            ticker.Ticker.EPS = ((1 - coeff) * eps) + (coeff * eps * epsGrowth);
                                        }
                                        else
                                        {
                                            var previousEPS = Math.Min(eps, eps / epsGrowth);
                                            ticker.Ticker.EPS = (eps + previousEPS) / 2;
                                        }
                                    }
                                    else
                                    {
                                        ticker.Ticker.EPS = Math.Min(eps, eps * 0.8);
                                    }
                                    if(epsFYIndex.HasValue && double.TryParse(csv[epsFYIndex.Value], out var epsFY) && epsFY > 0)
                                    {
                                        ticker.Ticker.EPS *= (eps / epsFY).ZeroReduce(2, 0);
                                    }
                                    else
                                    {
                                        ticker.Ticker.EPS *= 0.9;
                                    }
                                }
                                else
                                {
                                    //_logger.LogError($"EPS not found for {ticker.Ticker.Symbol}");
                                }
                                if (sharesIndex.HasValue && double.TryParse(csv[sharesIndex.Value], out var shares))
                                {
                                    ticker.Ticker.Shares = shares;
                                    if(ticker.Ticker.EPS > 0)
                                    {
                                        ticker.Ticker.Earnings = ticker.Ticker.EPS * shares;
                                    }
                                }
                                else
                                {
                                    _logger.LogError($"No shares outstanding for {ticker.Ticker.Symbol} to calculate earnings.");
                                }
                                if (priceIndex.HasValue && double.TryParse(csv[priceIndex.Value], out var price))
                                {
                                    ticker.Price = price;
                                    ticker.Ticker.PERatio = ticker.Ticker.EPS > 0 ? (price / ticker.Ticker.EPS) : ticker.Ticker.PERatio;
                                }
                                else
                                {
                                    //_logger.LogWarning($"Price not found for {ticker.Ticker.Symbol}");
                                }
                                if(recentEarningsIndex.HasValue && DateTime.TryParse(csv[recentEarningsIndex.Value], out var recentEarningsDate))
                                {
                                    ticker.RecentEarningsDate = recentEarningsDate;
                                }
                                else
                                {
                                    _logger.LogWarning($"Recent Earnings Date not found for {ticker.Ticker.Symbol}");
                                }
                                if (ticker.Ticker.Earnings > 0)
                                {
                                    if (evIndex.HasValue && double.TryParse(csv[evIndex.Value], out var ev))
                                    {
                                        ticker.EV = ev;
                                        ticker.Ticker.EVEarnings = ev / ticker.Ticker.Earnings;
                                    }
                                    else if (evebitdaIndex.HasValue && double.TryParse(csv[evebitdaIndex.Value], out var evebitda) &&
                                             ebitdaIndex.HasValue && double.TryParse(csv[ebitdaIndex.Value], out var ebitda))
                                    {
                                        ticker.EV = evebitda * ebitda;
                                        ticker.Ticker.EVEarnings = ticker.EV / ticker.Ticker.Earnings;
                                    }
                                    else
                                    {
                                        //_logger.LogWarning($"EV EBITDA ratio unavailable for {ticker.Ticker.Symbol}. Could not calculate EV.");
                                    }
                                    if (marketCapIndex.HasValue && double.TryParse(csv[marketCapIndex.Value], out var marketCap))
                                    {
                                        ticker.Ticker.MarketCap = marketCap;
                                    }
                                    else if(ticker.Ticker.Shares > 0 && ticker.Price > 0)
                                    {
                                        _logger.LogWarning($"Market cap not found for {ticker.Ticker.Symbol}. Deriving from shares*price");
                                        ticker.Ticker.MarketCap = ticker.Ticker.Shares * ticker.Price;
                                    }
                                    else
                                    {
                                        _logger.LogError($"Market cap not found for {ticker.Ticker.Symbol} and unable to derive.");
                                    }
                                    if (ticker.EV > 0)
                                    {
                                        ticker.Ticker.DebtMinusCash = ticker.EV - ticker.Ticker.MarketCap;
                                    }
                                    else
                                    {
                                        ticker.Ticker.DebtMinusCash = ticker.Ticker.MarketCap; //make this large to invalidate ticker
                                        //_logger.LogWarning($"Unable to calculate DebtMinusCash for {ticker.Ticker.Symbol}. EV not found.");
                                    }
                                }
                                else if(ticker.Ticker.Earnings == 0)
                                {
                                    //_logger.LogError($"Earnings ratio unavailable for {ticker.Ticker.Symbol}. Earnings not found.");
                                }
                                tickers.Add(ticker);
                            }
                        }
                    }
                }
                await _tickerHash.WriteNewHash(currentHash);
            }
            return new TickerBankProcessorResult
            {
                LastModified = await _tickerFile.GetLastModified(),
                BankTickers = tickers.ToArray()
            };
        }
        //private static ProcessorBankTicker[] FinalCalc(List<ProcessorBankTicker> tickers)
        //{
        //    if(tickers.Count == 0) { return new ProcessorBankTicker[] { }; }
        //    var orderedTickers = tickers.OrderByDescending(t => t.Ticker.MarketCap).ToArray();
        //    for (var i = 0; i < orderedTickers.Length; i++)
        //    {
        //        var ticker = orderedTickers[i];
        //        var eps = ticker.Ticker.EPS;
        //        if (ticker.EPSGrowth.HasValue && eps > 0)
        //        {
        //            var epsGrowth = (ticker.EPSGrowth.Value + 100) / 100;
        //            var previousEPS = eps / epsGrowth;
        //            var avgEPS = (eps + previousEPS) / 2;
        //            if (epsGrowth < 0)
        //            {
        //                var curveExp = 2 + (Convert.ToDouble(i * 8) / orderedTickers.Length);
        //                var epsRatio = eps / avgEPS;
        //                var epsMultiplier = epsRatio.Curve4(curveExp);
        //                eps *= epsMultiplier;
        //            }
        //            else 
        //            {
        //                var curveExp = 1 + (Convert.ToDouble(i) / orderedTickers.Length);
        //                var epsRatio = (avgEPS - ticker.CurrentEPS) / ticker.CurrentEPS;
        //                var epsMultiplier = epsRatio.Curve1(curveExp) + 1;
        //                eps *= epsMultiplier;
        //            }
        //            ticker.Ticker.EPS = eps;
        //            ticker.Ticker.Earnings = eps * ticker.Ticker.Shares;
        //            ticker.Ticker.PERatio = ticker.Price / eps;
        //            ticker.Ticker.EVEarnings = ticker.EV / ticker.Ticker.Earnings;
        //        }
        //    }
        //    return orderedTickers;
        //}
        public void UpdateBankTicker(BankTicker dest, BankTicker src)
        {
            dest.Sector = src.Sector;
            dest.MarketCap = src.MarketCap;
            dest.CurrentRatio = src.CurrentRatio;
            dest.DebtEquityRatio = src.DebtEquityRatio;
            dest.DividendYield = src.DividendYield;
            dest.Earnings = src.Earnings;
            dest.EPS = src.EPS;
            dest.PERatio = src.PERatio;
            dest.EVEarnings = src.EVEarnings;
            dest.PriceChangeAvg = src.PriceChangeAvg;
            dest.DebtMinusCash = src.DebtMinusCash;
            dest.Shares = src.Shares;
            dest.Country = src.Country;
            dest.LastCalculatedFinancials = src.LastCalculatedFinancials;
            dest.LastCalculatedFinancialsMillis = src.LastCalculatedFinancialsMillis;
        }
    }
    public class TickerBankProcessorResult
    {
        public DateTimeOffset? LastModified { get; set; }
        public ProcessorBankTicker[] BankTickers { get; set; }
    }
    public class ProcessorBankTicker
    {
        public ProcessorBankTicker()
        {
            Ticker = new BankTicker();
        }
        public double? RevenueGrowth { get; set; }
        public double? Income { get; set; }
        public double? QoQEPSGrowth { get; set; }
        public double? YoYEPSGrowth { get; set; }
        public double Price { get; set; }
        public double EV { get; set; }
        public DateTime? RecentEarningsDate { get; set; }
        public BankTicker Ticker { get; }
    }
}
