using Alpaca.Markets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using NumbersGoUp.Utils;
using NumbersGoUp.JsonModels;
using NumbersGoUp.Models;
using Alpaca.Markets.Extensions;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.WebSockets;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;

namespace NumbersGoUp.Services
{
    public class AlpacaService : IBrokerService
    {
        private const bool IS_FREE_PLAN = true;
        public const int CALENDAR_LOOKBACK_MONTHS = 1;
        private readonly ILogger<AlpacaService> _logger;
        private readonly IAppCancellation _appCancellation;

        private IAlpacaTradingClient _tradingClient;

        private IAlpacaDataClient _dataClient;

        private readonly RateLimiter _rateLimiter;
        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment _environment;
        private IIntervalCalendar _lastMarketDay;
        private Task _startTask;
        private static readonly SemaphoreSlim _taskSem = new SemaphoreSlim(1, 1);
        private IAccount _account;
        private IIntervalCalendar[] _calendars;
        private IClock _lastClock;

        public AlpacaService(ILogger<AlpacaService> logger, IHostEnvironment environment, IAppCancellation appCancellation, RateLimiter rateLimiter, IConfiguration configuration)
        {
            _appCancellation = appCancellation;
            _logger = logger;
            _environment = environment;
            _rateLimiter = rateLimiter;
            _configuration = configuration;
        }

        public async Task Init()
        {
            _logger.LogInformation($"Running in {_environment.EnvironmentName} mode");
            var apiKeyDev = _configuration[$"alpaca_api_key:{_environment.EnvironmentName}"];
            var apiSecretDev = _configuration[$"alpaca_api_secret:{_environment.EnvironmentName}"];
            var apiKeyProd = _configuration[$"alpaca_api_key:{Microsoft.Extensions.Hosting.Environments.Production}"];
            var apiSecretProd = _configuration[$"alpaca_api_secret:{Microsoft.Extensions.Hosting.Environments.Production}"];
            _tradingClient = _environment.IsProduction() ? Alpaca.Markets.Environments.Live.GetAlpacaTradingClient(new SecretKey(apiKeyProd, apiSecretProd)) :
                                                           Alpaca.Markets.Environments.Paper.GetAlpacaTradingClient(new SecretKey(apiKeyDev, apiSecretDev));

            _dataClient = Alpaca.Markets.Environments.Live.GetAlpacaDataClient(new SecretKey(apiKeyProd, apiSecretProd));

            try
            {
                _account = await _tradingClient.GetAccountAsync(_appCancellation.Token);
                var calendarRequest = new CalendarRequest().WithInterval(new Interval<DateTime>(DateTime.Now.AddMonths(0 - CALENDAR_LOOKBACK_MONTHS), (await GetMarketOpen()).Subtract(TimeSpan.FromDays(1))));
                _calendars = (await _tradingClient.ListIntervalCalendarAsync(calendarRequest, _appCancellation.Token)).OrderBy(calendar => new DateTimeOffset(calendar.GetTradingCloseTimeUtc(), TimeSpan.Zero).ToUnixTimeSeconds()).ToArray();
                _lastMarketDay = _calendars.Last();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Get account failure. Shutting down application as we can't really do anything");
                _ = _appCancellation.Shutdown();
                throw;
            }
            finally
            {
                _logger.LogInformation("Completed broker service initialization");
            }
        }
        private async Task TestHttpClient(string key, string secret)
        {
            //var client = new RestClient(new RestClientOptions
            //{
            //    BaseHost = "https://paper-api.alpaca.markets",
            //    UseDefaultCredentials = true,
            //    RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => { return true; }
            //});
            //var request = new RestRequest("/v2/account", Method.Get);
            //request.AddHeader("APCA-API-KEY-ID", "PK2TLA13H3XF27AI6ACZ");
            //request.AddHeader("APCA-API-SECRET-KEY", "X34BcSPUldgNGGOg0HMyu3xOVFuYDxsH2Lrpauhb");
            //RestResponse response = await client.ExecuteAsync(request);
            //Console.WriteLine(response.Content);

            var httpClient = new HttpClient(new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12,
                    RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => { return true; },
                    //CipherSuitesPolicy = new CipherSuitesPolicy(new List<TlsCipherSuite> { TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 })
                }
            })
            {
                BaseAddress = new UriBuilder("https://paper-api.alpaca.markets").Uri
                //BaseAddress = new UriBuilder("https://www.google.com").Uri
            };
            httpClient.DefaultRequestHeaders.Add("APCA-API-KEY-ID", key);
            httpClient.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", secret);
            //httpClient.DefaultRequestHeaders.Host = "paper-api.alpaca.markets";
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
            var response2 = await httpClient.GetAsync("/v2/account");
            //var response2 = await httpClient.GetAsync("/finance/quote/.INX:INDEXSP");
            _logger.LogInformation(await response2.Content.ReadAsStringAsync());
            response2.EnsureSuccessStatusCode();
            //_logger.LogInformation(await response.Content.ReadAsStringAsync());
        }

        public async Task Ready()
        {
            if (_startTask == null)
            {
                await _taskSem.WaitAsync();
                try
                {
                    if (_startTask == null)
                    {
                        _startTask = Task.Run(Init);
                    }
                }
                finally
                {
                    _taskSem.Release();
                }
            }
            await _startTask;
        }
        public async Task<IEnumerable<HistoryBar>> GetBarHistoryDay(string symbol, DateTime from)
        {
            await Ready();
            var into = _lastMarketDay.GetTradingCloseTimeUtc();
            var now = DateTime.Now;
            if (IS_FREE_PLAN && into.CompareTo(now.AddMinutes(-16)) > 0)
            {
                into = into.AddDays(-1);
            }
            var request = new HistoricalBarsRequest(symbol, from, into, BarTimeFrame.Day)
            {
                Adjustment = Adjustment.SplitsOnly
            };
            var historyBars = new List<HistoryBar>();
            do
            {
                IPage<IBar> bars = null;
                for(var i = 0; i < 4; i++)
                {
                    try
                    {
                        await _rateLimiter.LimitAlpacaDataRate();
                        bars = await _dataClient.ListHistoricalBarsAsync(request, _appCancellation.Token);
                        request = request.WithPageToken(bars.NextPageToken);
                        break;
                    }
                    catch (Exception e)
                    {
                        if(i == 3) 
                        {
                            _logger.LogError(e, "Error retrieving historical bars");
                            throw;
                        }
                        else
                        {
                            await Task.Delay(500);
                        }
                    }
                }
                if(bars != null)
                {
                    foreach (var bar in bars.Items)
                    {
                        var volume = (long)bar.Volume;
                        volume = volume < 0 ? long.MaxValue : volume;
                        historyBars.Add(new HistoryBar
                        {
                            Symbol = bar.Symbol,
                            ClosePrice = decimal.ToDouble(bar.Close),
                            HighPrice = decimal.ToDouble(bar.High),
                            LowPrice = decimal.ToDouble(bar.Low),
                            OpenPrice = decimal.ToDouble(bar.Open),
                            Volume = volume,
                            BarDay = bar.TimeUtc,
                            BarDayMilliseconds = new DateTimeOffset(bar.TimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds()
                        });
                    }
                }
            } while (request.Pagination.Token != null);
            return historyBars;
        }

        public async Task<TickerInfo> GetTickerInfo(string symbol)
        {
            await Ready();
            await _rateLimiter.LimitAlpacaTraderRate();
            try
            {
                var info = await _tradingClient.GetAssetAsync(symbol, _appCancellation.Token);
                if (info.Status == AssetStatus.Active)
                {
                    return new TickerInfo
                    {
                        IsTradable = info.IsTradable,
                        EasyToBorrow = info.EasyToBorrow,
                        Fractionable = info.Fractionable,
                        Marginable = info.Marginable,
                        Name = info.Name,
                        Shortable = info.Shortable,
                        Symbol = symbol
                    };
                }
            }
            catch {
                _logger.LogError($"Error getting ticker info for {symbol}!");
            }
            return null;
        }

        public async Task<Quote> GetLastTrade(string symbol)
        {
            await Ready();
            await _rateLimiter.LimitAlpacaDataRate();
            var lastTrade = await _dataClient.GetLatestTradeAsync(new LatestMarketDataRequest(symbol), _appCancellation.Token);
            var size = (long)lastTrade.Size;
            size = size < 0 ? long.MaxValue : size;
            return new Quote
            {
                Price = Convert.ToDouble(lastTrade.Price),
                Size = size,
                Symbol = lastTrade.Symbol,
                TradeTime = lastTrade.TimestampUtc,
                TradeTimeMilliseconds = new DateTimeOffset(lastTrade.TimestampUtc, TimeSpan.Zero).ToUnixTimeMilliseconds()
            };
        }

        public async Task<IEnumerable<Position>> GetPositions()
        {
            await Ready();
            await _rateLimiter.LimitAlpacaTraderRate();
            var positions = await _tradingClient.ListPositionsAsync(_appCancellation.Token);
            return positions.Select(position => new Position
            {
                AssetCurrentPrice = Convert.ToDouble(position.AssetCurrentPrice),
                AssetLastPrice = Convert.ToDouble(position.AssetLastPrice),
                AverageEntryPrice = Convert.ToDouble(position.AverageEntryPrice),
                CostBasis = Convert.ToDouble(position.CostBasis),
                MarketValue = Convert.ToDouble(position.MarketValue),
                Quantity = Convert.ToDouble(position.Quantity),
                UnrealizedProfitLoss = Convert.ToDouble(position.UnrealizedProfitLoss),
                UnrealizedProfitLossPercent = Convert.ToDouble(position.UnrealizedProfitLossPercent),
                Symbol = position.Symbol
            }).ToArray();
        }
        public async Task<Position> GetPosition(string symbol)
        {
            await Ready();
            await _rateLimiter.LimitAlpacaTraderRate();
            var position = await _tradingClient.GetPositionAsync(symbol, _appCancellation.Token);
            return new Position
            {
                AssetCurrentPrice = Convert.ToDouble(position.AssetCurrentPrice),
                AssetLastPrice = Convert.ToDouble(position.AssetLastPrice),
                AverageEntryPrice = Convert.ToDouble(position.AverageEntryPrice),
                CostBasis = Convert.ToDouble(position.CostBasis),
                MarketValue = Convert.ToDouble(position.MarketValue),
                Quantity = Convert.ToDouble(position.Quantity),
                UnrealizedProfitLoss = Convert.ToDouble(position.UnrealizedProfitLoss),
                UnrealizedProfitLossPercent = Convert.ToDouble(position.UnrealizedProfitLossPercent),
                Symbol = position.Symbol
            };
        }

        // Submit an order if quantity is not zero.
        public async Task<BrokerOrder> BuyAdvanced(string symbol, int qty, double price, double? takeProfit = null, double? stopLoss = null)
        {
            await Ready();
            await _rateLimiter.LimitAlpacaTraderRate();
            try
            {
                if (qty < 1)
                {
                    Console.WriteLine("No order necessary.");
                    return null;
                }
                _logger.LogInformation($"Submitting buy order for {qty} shares of {symbol} at ${price}.");
                var order = Alpaca.Markets.OrderSide.Buy.Limit(symbol, qty, Convert.ToDecimal(price));
                IOrder alpacaOrder;
                if(takeProfit.HasValue && stopLoss.HasValue)
                {
                    alpacaOrder = await _tradingClient.PostOrderAsync(order.Bracket(Convert.ToDecimal(takeProfit.Value), Convert.ToDecimal(stopLoss)), _appCancellation.Token);
                }
                else if (takeProfit.HasValue)
                {
                    alpacaOrder = await _tradingClient.PostOrderAsync(order.TakeProfit(Convert.ToDecimal(takeProfit.Value)), _appCancellation.Token);
                }
                else if (stopLoss.HasValue)
                {
                    alpacaOrder = await _tradingClient.PostOrderAsync(order.StopLoss(Convert.ToDecimal(stopLoss.Value)), _appCancellation.Token);
                }
                else
                {
                    alpacaOrder = await _tradingClient.PostOrderAsync(order, _appCancellation.Token);
                }
                if(alpacaOrder.OrderStatus != Alpaca.Markets.OrderStatus.Rejected)
                {
                    return new BrokerOrder
                    {
                        BrokerOrderId = alpacaOrder.OrderId.ToString(),
                        ClientOrderId = alpacaOrder.ClientOrderId,
                        OrderSide = Models.OrderSide.Buy,
                        Symbol = symbol
                    };
                }
            }
            catch(Exception e)
            {
                _logger.LogError(e, $"Error buying {qty} shares of {symbol} at price {price:C2}");
            }
            return null;
        }
        public async Task<BrokerOrder> SellAdvanced(string symbol, int qty, double price, double? stopLoss = null)
        {
            await Ready();
            await _rateLimiter.LimitAlpacaTraderRate();
            try
            {
                if (qty < 1)
                {
                    Console.WriteLine("No order necessary.");
                    return null;
                }
                _logger.LogInformation($"Submitting sell order for {qty} shares of {symbol} at ${price}.");
                var order = Alpaca.Markets.OrderSide.Sell.Limit(symbol, qty, Convert.ToDecimal(price));
                IOrder alpacaOrder;
                if (stopLoss.HasValue)
                {
                    alpacaOrder = await _tradingClient.PostOrderAsync(order.OneCancelsOther(Convert.ToDecimal(stopLoss.Value), Convert.ToDecimal(stopLoss.Value * 0.9996)), _appCancellation.Token);
                }
                else
                {
                    alpacaOrder = await _tradingClient.PostOrderAsync(order, _appCancellation.Token);
                }
                if (alpacaOrder.OrderStatus != Alpaca.Markets.OrderStatus.Rejected)
                {
                    return new BrokerOrder
                    {
                        BrokerOrderId = alpacaOrder.OrderId.ToString(),
                        ClientOrderId = alpacaOrder.ClientOrderId,
                        OrderSide = Models.OrderSide.Sell,
                        Symbol = symbol
                    };
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error selling {qty} shares of {symbol} at price {price:C2}");
            }
            return null;
        }
        public async Task<BrokerOrder> Sell(string symbol, double qty, double? limit = null)
        {
            if(limit != null)
            {
                return await SellAdvanced(symbol, (int)qty, limit.Value);
            }
            await Ready();
            await _rateLimiter.LimitAlpacaTraderRate();
            try
            {
                _logger.LogInformation($"Submitting sell order for {qty} shares of {symbol}");
                var order = new NewOrderRequest(symbol, OrderQuantity.Fractional(Convert.ToDecimal(qty)), Alpaca.Markets.OrderSide.Sell, Alpaca.Markets.OrderType.Market, TimeInForce.Day);
                var alpacaOrder = await _tradingClient.PostOrderAsync(order, _appCancellation.Token);
                if (alpacaOrder.OrderStatus != Alpaca.Markets.OrderStatus.Rejected)
                {
                    return new BrokerOrder
                    {
                        BrokerOrderId = alpacaOrder.OrderId.ToString(),
                        ClientOrderId = alpacaOrder.ClientOrderId,
                        OrderSide = Models.OrderSide.Sell,
                        Symbol = symbol
                    };
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error selling {qty} shares of {symbol}");
            }
            return null;
        }
        public async Task<BrokerOrder> Buy(string symbol, double qty, double? limit = null)
        {
            if (limit != null)
            {
                return await BuyAdvanced(symbol, (int)qty, limit.Value);
            }
            await Ready();
            await _rateLimiter.LimitAlpacaTraderRate();
            try
            {
                _logger.LogInformation($"Submitting buy order for {qty} shares of {symbol}");
                var order = new NewOrderRequest(symbol, OrderQuantity.Fractional(Convert.ToDecimal(qty)), Alpaca.Markets.OrderSide.Buy, Alpaca.Markets.OrderType.Market, TimeInForce.Day);
                var alpacaOrder = await _tradingClient.PostOrderAsync(order, _appCancellation.Token);
                if (alpacaOrder.OrderStatus != Alpaca.Markets.OrderStatus.Rejected)
                {
                    return new BrokerOrder
                    {
                        BrokerOrderId = alpacaOrder.OrderId.ToString(),
                        ClientOrderId = alpacaOrder.ClientOrderId,
                        OrderSide = Models.OrderSide.Buy,
                        Symbol = symbol
                    };
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error buying {qty} shares of {symbol}");
            }
            return null;
        }

        public async Task<BrokerOrder> ClosePositionAtMarket(string symbol)
        {
            await Ready();
            await _rateLimiter.LimitAlpacaTraderRate();
            try
            {
                var alpacaOrder = await _tradingClient.DeletePositionAsync(new DeletePositionRequest(symbol), _appCancellation.Token);
                if (alpacaOrder.OrderStatus != Alpaca.Markets.OrderStatus.Rejected)
                {
                    return new BrokerOrder
                    {
                        BrokerOrderId = alpacaOrder.OrderId.ToString(),
                        ClientOrderId = alpacaOrder.ClientOrderId,
                        OrderSide = Models.OrderSide.Sell,
                        Symbol = symbol
                    };
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error closing position for {symbol}");
            }
            return null;
        }
        public async Task<bool> AwaitMarketOpen()
        {
            await Ready();
            try
            {
                var clock = await GetClock();
                if (!clock.IsOpen)
                {
                    var minsToOpen = clock.NextOpenUtc.Subtract(DateTime.UtcNow).TotalMinutes;
                    if (minsToOpen < 80) //if market isn't going to open soonish, don't bother waiting
                    {
                        while (!_appCancellation.IsCancellationRequested && !clock.IsOpen)
                        {
                            _logger.LogInformation("Market not open. waiting");
                            try
                            {
                                await Task.Delay(60000, _appCancellation.Token);
                                clock = await GetClock();
                            }
                            catch (TaskCanceledException) { }
                        }
                        _logger.LogInformation($"market open: {clock.IsOpen}");
                        return clock.IsOpen;
                    }
                    else
                    {
                        if (minsToOpen < 500)_logger.LogInformation("To early for market"); else _logger.LogInformation("Market closed");
                        return false;
                    }
                }
                else
                {
                    return true;
                }
            }
            catch (TaskCanceledException) 
            {
                _logger.LogInformation($"{nameof(AwaitMarketOpen)} exiting as application is shutting down");
                return false;
            }
        }
        public async Task<bool> AwaitBuySell(DateTime waitUntil)
        {
            await Ready();
            if(await AwaitMarketOpen())
            {
                try
                {
                    var timeToWait = waitUntil.Subtract(DateTime.Now).TotalMinutes;
                    while (!_appCancellation.IsCancellationRequested && timeToWait > 0)
                    {
                        if (timeToWait < 20) { await Task.Delay(60000, _appCancellation.Token); } 
                        else { await Task.Delay(Convert.ToInt32((timeToWait - 20.0) * 60000), _appCancellation.Token); }
                    }
                    return true;
                }
                catch (TaskCanceledException)
                {
                    _logger.LogInformation($"{nameof(AwaitBuySell)} exiting as application is shutting down");
                    return false;
                }
            }
            return false;
        }

        public void Dispose()
        {
            _tradingClient?.Dispose();
            _dataClient?.Dispose();
        }

        public async Task<IEnumerable<BrokerOrder>> GetOpenOrders()
        {
            await Ready();
            await _rateLimiter.LimitAlpacaTraderRate();
            var orders = await _tradingClient.ListOrdersAsync(new ListOrdersRequest
            {
                OrderStatusFilter = OrderStatusFilter.Open,
                LimitOrderNumber = 200,
                OrderListSorting = SortDirection.Descending
            }, _appCancellation.Token);
            return orders.Select(o => new BrokerOrder
            {
                BrokerOrderId = o.OrderId.ToString(),
                ClientOrderId = o.ClientOrderId,
                OrderSide = o.OrderSide == Alpaca.Markets.OrderSide.Buy ? Models.OrderSide.Buy : Models.OrderSide.Sell,
                Symbol = o.Symbol
            });
        }
        public async Task<IEnumerable<BrokerOrder>> GetClosedOrders(DateTime? from = null)
        {
            await Ready();
            await _rateLimiter.LimitAlpacaTraderRate();
            var request = new ListOrdersRequest
            {
                OrderStatusFilter = OrderStatusFilter.Closed,
                LimitOrderNumber = 200,
                OrderListSorting = SortDirection.Descending
            };
            var orders = await _tradingClient.ListOrdersAsync(request.WithInterval(new Interval<DateTime>(from, DateTime.Now)), _appCancellation.Token);
            return orders.Select(o => new BrokerOrder
            {
                BrokerOrderId = o.OrderId.ToString(),
                ClientOrderId = o.ClientOrderId,
                OrderSide = o.OrderSide == Alpaca.Markets.OrderSide.Buy ? Models.OrderSide.Buy : Models.OrderSide.Sell,
                Symbol = o.Symbol,
                AverageFillPrice = o.AverageFillPrice.ToDouble(),
                FilledAt = o.FilledAtUtc,
                FilledQuantity = o.FilledQuantity.ToDouble(),
                OrderStatus = GetBrokerOrderStatus(o)
            });
        }
        private Models.OrderStatus GetBrokerOrderStatus(IOrder order)
        {
            if (order.OrderStatus == Alpaca.Markets.OrderStatus.Filled) return Models.OrderStatus.FILLED;
            else if (order.OrderStatus == Alpaca.Markets.OrderStatus.PartiallyFilled) return Models.OrderStatus.PARTIALLY_FILLED;
            else return Models.OrderStatus.NOT_FILLED;
        }

        public async Task<DateTime> GetMarketOpen()
        {
            var clock = await GetClock();
            if (clock.IsOpen)
            {
                await _rateLimiter.LimitAlpacaTraderRate();
                var calendar = await _tradingClient.GetCalendarForSingleDayAsync(DateOnly.FromDateTime(DateTime.Now), _appCancellation.Token);
                return calendar.GetTradingOpenTimeUtc();
            }
            return clock.NextOpenUtc;
        }

        public async Task<Models.MarketDay> GetLastMarketDay()
        {
            await Ready();
            return new Models.MarketDay
            {
                TradingTimeClose = _lastMarketDay.GetTradingCloseTimeEst(),
                TradingTimeOpen = _lastMarketDay.GetTradingOpenTimeEst()
            };
        }

        public async Task<DateTime> GetMarketClose()
        {
            await Ready();
            var clock = await GetClock();
            return clock.NextCloseUtc;
        }

        public async Task<Models.Account> GetAccount()
        {
            await Ready();
            return new Models.Account
            {
                Balance = new Models.Balance
                {
                    BuyingPower = _account.BuyingPower.ToDouble(),
                    LastEquity = _account.LastEquity.ToDouble(),
                    TradableCash = _account.TradableCash.ToDouble()
                }
            };
        }
        public async Task<IPortfolioHistory> GetPortfolioHistory(HistoryPeriodUnit period)
        {
            await Ready();
            await _rateLimiter.LimitAlpacaTraderRate();
            var request = new PortfolioHistoryRequest
            {
                Period = new HistoryPeriod(1, period)
            };
            return await _tradingClient.GetPortfolioHistoryAsync(request, _appCancellation.Token);
        }
        public Task<Financials> GetFinancials(string symbol) => Task.FromResult<Financials>(null);
        private async Task<IClock> GetClock()
        {
            await _rateLimiter.LimitAlpacaTraderRate();
            try
            {
                _lastClock = await _tradingClient.GetClockAsync(_appCancellation.Token);
            }
            catch(Exception e)
            {
                if(_lastClock != null)
                {
                    _logger.LogError("Error retrieving most recent clock. Using old one");
                }
                else
                {
                    _logger.LogError(e, "Error retrieving most recent clock");
                    throw;
                }
            }
            return _lastClock;
        }

        public Task<BrokerOrder> GetOrder(string orderId)
        {
            throw new NotImplementedException();
        }

        public Task<IEnumerable<Models.MarketDay>> GetMarketDays(int year, int? month = null)
        {
            throw new NotImplementedException();
        }
        public Task<(Dictionary<string, List<AccountHistoryEvent>> trades, double dividends)> GetAccountHistory()
        {
            throw new NotImplementedException();
        }
    }
}
