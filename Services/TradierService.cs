using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NumbersGoUp.Models;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NumbersGoUp.Services
{
    public class TradierService : IBrokerService
    {
        private const string SANDBOX_URL = "sandbox.tradier.com";
        private const string PRODUCTION_URL = "api.tradier.com";
        private const string CASH_MIN = "CashMinimum";

        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TradierService> _logger;
        private readonly IHostEnvironment _environment;
        private readonly RateLimiter _rateLimiter;
        private readonly HttpClient _tradierClient;
        private readonly IConfiguration _configuration;
        private Account _account;
        private MarketDay _marketDay;
        private MarketDay _lastMarketDay;
        private Task _startTask;
        private static readonly SemaphoreSlim _taskSem = new SemaphoreSlim(1, 1);

        private string ProfilePath = "/v1/user/profile";
        private string AccountPath => $"/v1/accounts/{_account.AccountId}/balances";
        private string CalendarPath => "/v1/markets/calendar?month={0}&year={1}";
        private string HistoryBarPath => "/v1/markets/history?symbol={0}&interval=daily&start={1}&end={2}";
        private string OrdersPath => $"/v1/accounts/{_account.AccountId}/orders";
        private string OrderPath => $"/v1/accounts/{_account.AccountId}/orders/{{0}}?includeTags=true";
        private string QuotesPath => "/v1/markets/quotes?symbols={0}";
        private string PositionsPath => $"/v1/accounts/{_account.AccountId}/positions";
        private string LookupPath => "/v1/markets/lookup?q={0}";
        private string FinancialsPath => "/beta/markets/fundamentals/financials?symbols={0}";

        public TradierService(ILogger<TradierService> logger, IHostEnvironment environment, IAppCancellation appCancellation, RateLimiter rateLimiter, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _appCancellation = appCancellation;
            _logger = logger;
            _environment = environment;
            _rateLimiter = rateLimiter;
            _tradierClient = httpClientFactory.CreateClient("retry");
            _configuration = configuration;
        }
        private async Task Init()
        {
            var now = DateTime.Now;
            _logger.LogInformation($"Running in {_environment.EnvironmentName} mode");
            var token = _configuration[$"tradier_token:{_environment.EnvironmentName}"];

            _tradierClient.BaseAddress = new Uri($"https://{(_environment.IsProduction() ? PRODUCTION_URL : SANDBOX_URL)}");
            _tradierClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _tradierClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            try
            {
                await _rateLimiter.LimitTradierRate();
                var profile = (await GetResponse<JsonModels.TradierProfileWrapper>(ProfilePath)).Profile;
                _account = new Account
                {
                    AccountId = profile.Accounts.FirstOrDefault(a => a.Classification == "individual").AccountNumber
                };
                await _rateLimiter.LimitTradierRate();
                var balances = await GetResponse<JsonModels.TradierAccountBalance>(AccountPath);
                if(balances.Balance.Cash == null)
                {
#if !DEBUG
                    _logger.LogError("Cash balance unavailable. Manual intervention required");
#endif
                }
                double.TryParse(_configuration[CASH_MIN], out var cashMinimum);
                _account.Balance = new Balance
                {
                    BuyingPower = balances.Balance.Margin?.BuyingPower ?? 0.0,
                    LastEquity = balances.Balance.Equity,
                    TradableCash = balances.Balance.Cash != null ? balances.Balance.Cash.CashAvailable - balances.Balance.Cash.UnsettledFunds - cashMinimum : 0.0
                };
                await LoadMarketDays(now.Month, now.Year);
                if (_marketDay == null) //if null, it means we're on the last day of the month
                {
                    if (now.Month == 12) { await LoadMarketDays(1, now.Year + 1); }
                    else { await LoadMarketDays(now.Month + 1, now.Year); }
                }
                if(_marketDay == null) { _logger.LogError("Unable to retrieve current market day"); }
                else { _logger.LogInformation($"Using current market day {_marketDay.Date.ToString("yyyy-MM-dd HH:mm:ss")}"); }
                if (_lastMarketDay == null) { _logger.LogError("Unable to retrieve previous market day"); }
                else { _logger.LogInformation($"Using previous market day {_lastMarketDay.Date.ToString("yyyy-MM-dd HH:mm:ss")}"); }
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
        public async Task Ready()
        {
            if(_startTask == null)
            {
                await _taskSem.WaitAsync();
                try
                {
                    if(_startTask == null)
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
        private async Task LoadMarketDays(int month, int year)
        {
            var now = DateTime.Now;
            await _rateLimiter.LimitTradierRate();
            var calendar = await GetResponse<JsonModels.TradierCalendarWrapper>(string.Format(CalendarPath, month.ToString("00"), year));
            var days = calendar.Calendar.DayWrapper.Days.Where(d => d.Status == "open").Select(day => new MarketDay
            {
                TradingTimeOpen = day.TradingTimeOpenEST,
                TradingTimeClose = day.TradingTimeCloseEST
            }).ToArray().OrderBy(d => d.TradingTimeClose).ToArray();
            for (var i = 0; i < days.Length; i++)
            {
                if (now.CompareTo(days[i].TradingTimeClose) < 0)
                {
                    _marketDay = days[i];
                    if (i > 0)
                    {
                        _lastMarketDay = days[i - 1];
                    }
                    else // in this case, we're on first day of month. get previous month and retry
                    {
                        await _rateLimiter.LimitTradierRate();
                        var previousCalendar = now.Month > 1 ? await GetResponse<JsonModels.TradierCalendarWrapper>(string.Format(CalendarPath, (now.Month - 1).ToString("00"), now.Year)) :
                                                               await GetResponse<JsonModels.TradierCalendarWrapper>(string.Format(CalendarPath, 12, now.Year - 1));
                        var previousDays = previousCalendar.Calendar.DayWrapper.Days.Where(d => d.Status == "open").Select(day => new MarketDay
                        {
                            TradingTimeOpen = day.TradingTimeOpenEST,
                            TradingTimeClose = day.TradingTimeCloseEST
                        }).ToArray().OrderByDescending(d => d.TradingTimeClose).ToArray();
                        _lastMarketDay = previousDays[0];
                    }
                    break;
                }
            }
        }

        public async Task<BrokerOrder> Buy(string symbol, double qty, double? limit = null)
        {
            await _rateLimiter.LimitTradierTradeRate();
            await Ready();
            var roundedQty = (int) qty;
            if(roundedQty == 0)
            {
                _logger.LogWarning($"Attempted to buy {symbol} with qty < 1");
                return null;
            }
            var postOrder = TradierPostOrder.Buy(symbol, roundedQty, limit);
            try
            {
                var response = await PostOrder(OrdersPath, postOrder);
                if (response.OrderStatus == JsonModels.TradierPostOrderResponse.SUCCESS_ORDERSTATUS)
                {
                    return new BrokerOrder
                    {
                        BrokerOrderId = response.Id.ToString(),
                        ClientOrderId = postOrder.Tag,
                        OrderSide = OrderSide.Buy,
                        Symbol = symbol
                    };
                }
                else
                {
                    _logger.LogError($"Buy Order was not successfully process. Status: {response.OrderStatus}");
                }
            }
            catch(Exception e)
            {
                _logger.LogError(e, "Error while buying");
            }
            return null;
        }

        public async Task<BrokerOrder> Sell(string symbol, double qty, double? limit = null)
        {
            await Ready();
            await _rateLimiter.LimitTradierTradeRate();
            var roundedQty = (int) qty;
            if (roundedQty == 0)
            {
                _logger.LogWarning("Attempted to sell with qty < 1");
                return null;
            }
            var postOrder = TradierPostOrder.Sell(symbol, roundedQty, limit);
            try
            {
                var response = await PostOrder(OrdersPath, postOrder);
                if (response.OrderStatus == JsonModels.TradierPostOrderResponse.SUCCESS_ORDERSTATUS)
                {
                    return new BrokerOrder
                    {
                        BrokerOrderId = response.Id.ToString(),
                        ClientOrderId = postOrder.Tag,
                        OrderSide = OrderSide.Sell,
                        Symbol = symbol
                    };
                }
                else
                {
                    _logger.LogError($"Sell Order was not successfully process. Status: {response.OrderStatus}");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while selling");
            }
            return null;
        }

        public async Task<BrokerOrder> ClosePositionAtMarket(string symbol)
        {
            await Ready();
            await _rateLimiter.LimitTradierTradeRate();
            try
            {
                var position = await GetPosition(symbol);
                if(position == null)
                {
                    _logger.LogError($"Could not close position for {symbol}. No position found!");
                }
                return await Sell(symbol, position.Quantity);
            }
            catch(Exception e)
            {
                _logger.LogError(e, $"Error closing position for {symbol}");
            }
            return null;
        }

        public void Dispose()
        {
            _appCancellation.Cancel();
        }

        public async Task<Account> GetAccount()
        {
            await Ready();
            return _account;
        }

        public async Task<IEnumerable<HistoryBar>> GetBarHistoryDay(string symbol, DateTime from)
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            var jsonBars = await GetResponse<JsonModels.TradierHistoryBars>(string.Format(HistoryBarPath, symbol, from.ToString("yyyy-MM-dd"), _lastMarketDay.TradingTimeClose.ToString("yyyy-MM-dd")));
            return jsonBars?.History?.Quotes?.Select(b => new HistoryBar
            {
                Symbol = symbol,
                ClosePrice = b.Close,
                OpenPrice = b.Open,
                LowPrice = b.Low,
                HighPrice = b.High,
                Volume = b.Volume,
                BarDay = b.BarDay.ToUniversalTime(),
                BarDayMilliseconds = new DateTimeOffset(b.BarDay).ToUnixTimeMilliseconds()
            }).ToArray() ?? Enumerable.Empty<HistoryBar>();
        }

        public async Task<IEnumerable<BrokerOrder>> GetClosedOrders(DateTime? from = null)
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            var result = await GetResponse<JsonModels.TradierOrdersWrapper>($"{OrdersPath}?includeTags=true");
            var orders = result?.TradierOrders?.Orders?.Where(o => o.OrderStatus == JsonModels.TradierOrder.FILLED_ORDERSTATUS);
            if (from.HasValue)
            {
                orders = orders?.Where(o => o.TransactionDate.CompareTo(from.Value) > 0);
            }
            return orders?.Select(o => new BrokerOrder
            {
                AverageFillPrice = o.AvgFillPrice,
                BrokerOrderId = o.Id.ToString(),
                ClientOrderId = o.Tag,
                FilledAt = o.TransactionDate,
                FilledQuantity = o.Quantity,
                OrderSide = o.OrderSide == JsonModels.TradierOrder.BUY_ORDERSIDE ? OrderSide.Buy : (o.OrderSide == JsonModels.TradierOrder.SELL_ORDERSIDE ? OrderSide.Sell : throw new Exception("Unknown order side")),
                Symbol = o.Symbol,
                OrderStatus = OrderStatus.FILLED
            }).ToArray() ?? Enumerable.Empty<BrokerOrder>();
        }

        public async Task<BrokerOrder> GetOrder(string orderId)
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            var result = await GetResponse<JsonModels.TradierOrderWrapper>(string.Format(OrderPath, orderId));
            if(result?.Order != null)
            {
                var order = result.Order;
                var brokerOrder = new BrokerOrder
                {
                    BrokerOrderId = order.Id.ToString(),
                    ClientOrderId = order.Tag,
                    OrderSide = order.OrderSide == JsonModels.TradierOrder.BUY_ORDERSIDE ? OrderSide.Buy : (order.OrderSide == JsonModels.TradierOrder.SELL_ORDERSIDE ? OrderSide.Sell : throw new Exception("Unknown order side")),
                    Symbol = order.Symbol
                };
                if (order.OrderStatus == JsonModels.TradierOrder.FILLED_ORDERSTATUS || order.OrderStatus == JsonModels.TradierOrder.PARTIALLYFILLED_ORDERSTATUS)
                {
                    brokerOrder.AverageFillPrice = order.AvgFillPrice;
                    brokerOrder.FilledAt = order.TransactionDate;
                    brokerOrder.FilledQuantity = order.Quantity;
                    brokerOrder.OrderStatus = GetBrokerOrderStatus(order);
                }
                return brokerOrder;
            }
            return null;
        }

        public async Task<IEnumerable<BrokerOrder>> GetOpenOrders()
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            var result = await GetResponse<JsonModels.TradierOrdersWrapper>($"{OrdersPath}?includeTags=true");
            var orders = result?.TradierOrders?.Orders?.Where(o => o.OrderStatus == JsonModels.TradierOrder.OPEN_ORDERSTATUS || o.OrderStatus == JsonModels.TradierOrder.PENDING_ORDERSTATUS || o.OrderStatus == JsonModels.TradierOrder.PARTIALLYFILLED_ORDERSTATUS);
            return orders?.Select(o => new BrokerOrder
            {
                BrokerOrderId = o.Id.ToString(),
                ClientOrderId = o.Tag,
                OrderSide = o.OrderSide == JsonModels.TradierOrder.BUY_ORDERSIDE ? OrderSide.Buy : (o.OrderSide == JsonModels.TradierOrder.SELL_ORDERSIDE ? OrderSide.Sell : throw new Exception("Unknown order side")),
                Symbol = o.Symbol
            }).ToArray() ?? Enumerable.Empty<BrokerOrder>();
        }

        private OrderStatus GetBrokerOrderStatus(JsonModels.TradierOrder order)
        {
            if (order.OrderStatus == JsonModels.TradierOrder.FILLED_ORDERSTATUS) return OrderStatus.FILLED;
            else if (order.OrderStatus == JsonModels.TradierOrder.PARTIALLYFILLED_ORDERSTATUS) return OrderStatus.PARTIALLY_FILLED;
            else return OrderStatus.NOT_FILLED;
        }

        public async Task<Quote> GetLastTrade(string symbol)
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            var response = await GetResponse<JsonModels.TradierQuotesWrapper>(string.Format(QuotesPath, symbol));
            var quote = response?.TradierQuotes?.Quotes?.FirstOrDefault();
            if(quote == null)
            {
                throw new NullReferenceException($"Price information not found for symbol {symbol}!!");
            }
            return new Quote
            {
                Price = quote.Price,
                Size = quote.Size,
                Symbol = quote.Symbol,
                TradeTime = quote.TradeTime,
                TradeTimeMilliseconds = quote.TradeTimeMilliseconds
            };
        }
        public async Task<IEnumerable<Quote>> GetLastTrades(params string[] symbols)
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            var response = await GetResponse<JsonModels.TradierQuotesWrapper>(string.Format(QuotesPath, string.Join(',', symbols)));
            var quotes = response?.TradierQuotes?.Quotes;
            if (quotes == null)
            {
                throw new NullReferenceException($"Price information not found for symbols {string.Join(',', symbols)}!!");
            }
            return quotes.Select(quote => new Quote
            {
                Price = quote.Price,
                Size = quote.Size,
                Symbol = quote.Symbol,
                TradeTime = quote.TradeTime,
                TradeTimeMilliseconds = quote.TradeTimeMilliseconds
            }).ToArray();
        }

        public async Task<DateTime> GetMarketClose()
        {
            await Ready();
            return _marketDay.TradingTimeClose;
        }

        public async Task<DateTime> GetMarketOpen()
        {
            await Ready();
            return _marketDay.TradingTimeOpen;
        }
        public async Task<MarketDay> GetLastMarketDay()
        {
            await Ready();
            return _lastMarketDay;
        }
        public async Task<IEnumerable<MarketDay>> GetMarketDays(int year, int? month = null)
        {
            await Ready();
            if(month.HasValue)
            {
                await _rateLimiter.LimitTradierRate();
                var calendar = await GetResponse<JsonModels.TradierCalendarWrapper>(string.Format(CalendarPath, month.Value.ToString("00"), year));
                return calendar.Calendar.DayWrapper.Days.Where(d => d.Status == "open").Select(day => new MarketDay
                {
                    TradingTimeOpen = day.TradingTimeOpenEST,
                    TradingTimeClose = day.TradingTimeCloseEST
                }).ToArray().OrderBy(d => d.TradingTimeClose).ToArray();
            }
            else
            {
                var days = new List<MarketDay>();
                for(var i = 1; i < 13; i++)
                {
                    await _rateLimiter.LimitTradierRate();
                    var calendar = await GetResponse<JsonModels.TradierCalendarWrapper>(string.Format(CalendarPath, i.ToString("00"), year));
                    days.AddRange(calendar.Calendar.DayWrapper.Days.Where(d => d.Status == "open").Select(day => new MarketDay
                    {
                        TradingTimeOpen = day.TradingTimeOpenEST,
                        TradingTimeClose = day.TradingTimeCloseEST
                    }).ToArray().OrderBy(d => d.TradingTimeClose));
                }
                return days;
            }
        }

        public async Task<Position> GetPosition(string symbol) => (await GetPositions()).FirstOrDefault(p => p.Symbol == symbol);

        public async Task<IEnumerable<Position>> GetPositions()
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            var jmodelPositions = (await GetResponse<JsonModels.TradierPositionsWrapper>(PositionsPath))?.TradierPositions?.Positions;
            
            if(jmodelPositions != null && jmodelPositions.Length > 0)
            {
                var positions = new List<Position>();
                var symbols = jmodelPositions.Select(p => p.Symbol).ToArray();
                var quotes = await GetLastTrades(symbols);
                foreach(var jposition in jmodelPositions)
                {
                    var quote = quotes.FirstOrDefault(q => q.Symbol == jposition.Symbol);
                    var position = new Position
                    {
                        CostBasis = jposition.CostBasis,
                        Quantity = jposition.Quantity,
                        Symbol = jposition.Symbol,
                        MarketValue = quote != null ? quote.Price * jposition.Quantity : null,
                        AssetCurrentPrice = quote != null ? quote.Price : null,
                        AssetLastPrice = quote != null ? quote.Price : null,
                        AverageEntryPrice = jposition.CostBasis / jposition.Quantity,
                        UnrealizedProfitLoss = 0,
                        UnrealizedProfitLossPercent = 0
                    };
                    position.UnrealizedProfitLoss = position.MarketValue.HasValue ? position.MarketValue - position.CostBasis : null;
                    position.UnrealizedProfitLossPercent = position.UnrealizedProfitLoss.HasValue ? position.UnrealizedProfitLoss / position.CostBasis : null;
                    positions.Add(position);
                }
                return positions;
            }
            return Enumerable.Empty<Position>();
        }

        public async Task<TickerInfo> GetTickerInfo(string symbol)
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            try
            {
                var securities = (await GetResponse<JsonModels.TradierSecuritiesWrapper>(string.Format(LookupPath, symbol)))?.TradierSecurities?.Securities;
                if (securities != null && securities.Any())
                {
                    var security = securities.FirstOrDefault(s => s.Symbol == symbol);
                    if (security != null)
                    {
                        return new TickerInfo
                        {
                            IsTradable = true,
                            Name = security.Description,
                            Symbol = symbol
                        };
                    }
                    else
                    {
                        _logger.LogError($"Nothing matched symbol {symbol}. Closest found was {securities.First().Symbol}");
                    }
                }
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, $"TickerInfo exception for {symbol}");
            }
            return null;
        }
        public async Task<Financials> GetFinancials(string symbol)
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            try
            {
#if DEBUG
                var jsonStr = await GetFinancialsTest(symbol);
                var json = JsonConvert.DeserializeObject<JToken>(jsonStr);
#else
                var json = await GetResponse<JToken>(string.Format(FinancialsPath, symbol));
#endif
                return new Financials
                {
                    EPS = double.TryParse(((JValue)json.SelectTokens("$..period_12m.normalized_basic_e_p_s").FirstOrDefault())?.Value?.ToString() ?? null, out var eps) ? eps : null,
                    EBIT = double.TryParse(((JValue)json.SelectTokens("$..period_12m.e_b_i_t").FirstOrDefault())?.Value?.ToString() ?? null, out var ebit) ? ebit :
                           (double.TryParse(((JValue)json.SelectTokens("$..period_12m.pretax_income").FirstOrDefault())?.Value?.ToString() ?? null, out ebit) ? ebit : null),
                };
            }
            catch(Exception e)
            {
                _logger.LogError(e, "Error capturing financials in broker");
            }
            return null;
        }
#if DEBUG
        public async Task<string> GetFinancialsTest(string symbol)
        {
            var token = _configuration[$"tradier_token:Production"];
            using(var client = new HttpClient())
            {
                client.BaseAddress = new Uri($"https://{PRODUCTION_URL}");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
                using var response = await client.GetAsync(string.Format(FinancialsPath, symbol), _appCancellation.Token);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(_appCancellation.Token);
                return json;
            }
        }
#endif
        private async Task<T> GetResponse<T>(string path)
        {
            using var response = await _tradierClient.GetAsync(path, _appCancellation.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(_appCancellation.Token);
            return JsonConvert.DeserializeObject<T>(json);
        }
        private async Task<JsonModels.TradierPostOrderResponse> PostOrder(string path, TradierPostOrder order)
        {
            using var response = await _tradierClient.PostAsync(path, order.HttpContent(), _appCancellation.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(_appCancellation.Token);
            return JsonConvert.DeserializeObject<JsonModels.TradierPostOrderResponseWrapper>(json).Order;
        }

    }
}
