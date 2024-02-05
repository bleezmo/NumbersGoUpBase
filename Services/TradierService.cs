using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NumbersGoUp.JsonModels;
using NumbersGoUp.Models;
using NumbersGoUp.Utils;

namespace NumbersGoUp.Services
{
    public class TradierService : IBrokerService
    {
        private const string SANDBOX_URL = "sandbox.tradier.com";
        private const string PRODUCTION_URL = "api.tradier.com";
        private const string CASH_MIN = "CashMinimum";
        private const string CASH_PERC = "CashPerc";

        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TradierService> _logger;
        private readonly IHostEnvironment _environment;
        private readonly RateLimiter _rateLimiter;
        private readonly HttpClient _tradierDataClient;
        private readonly HttpClient _tradierOrderClient;
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
        private string AccountHistoryPath => $"/v1/accounts/{_account.AccountId}/history?limit=500&page={{0}}";

        public TradierService(ILogger<TradierService> logger, IHostEnvironment environment, IAppCancellation appCancellation, RateLimiter rateLimiter, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _appCancellation = appCancellation;
            _logger = logger;
            _environment = environment;
            _rateLimiter = rateLimiter;
            _tradierDataClient = httpClientFactory.CreateClient("retry");
            _tradierOrderClient = httpClientFactory.CreateClient();
            _configuration = configuration;
        }
        private void SetTradierHttpClients(string token, params HttpClient[] clients)
        {
            foreach(var client in clients)
            {
                client.BaseAddress = new Uri($"https://{(_environment.IsProduction() ? PRODUCTION_URL : SANDBOX_URL)}");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            }
        }
        private async Task Init()
        {
            var now = DateTime.Now;
            _logger.LogInformation($"Running in {_environment.EnvironmentName} mode");

            SetTradierHttpClients(_configuration[$"tradier_token:{_environment.EnvironmentName}"], _tradierDataClient, _tradierOrderClient);

            try
            {
                await _rateLimiter.LimitTradierRate();
                var profile = (await GetResponse<TradierProfileWrapper>(ProfilePath)).Profile;
                _account = new Account
                {
                    AccountId = profile.Accounts.FirstOrDefault(a => a.Classification == "individual").AccountNumber
                };
                await _rateLimiter.LimitTradierRate();
                var balances = await GetResponse<TradierAccountBalance>(AccountPath);
                if(balances.Balance.Cash == null && balances.Balance.Margin == null)
                {
#if !DEBUG
                    _logger.LogError("Balance unavailable. Manual intervention required");
#endif
                }
                double.TryParse(_configuration[CASH_MIN], out var cashMinimum);
                if(double.TryParse(_configuration[CASH_PERC], out var cashPerc))
                {
                    cashMinimum = Math.Max(cashMinimum, cashPerc.DoubleReduce(1, 0) * balances.Balance.Equity);
                }
                var cashAvailable = balances.Balance.Cash != null ? (balances.Balance.Cash.CashAvailable - balances.Balance.Cash.UnsettledFunds) : (balances.Balance.Margin != null ? balances.Balance.TotalCash : 0.0);
                _account.Balance = new Balance
                {
                    BuyingPower = balances.Balance.Margin?.BuyingPower ?? 0.0,
                    LastEquity = balances.Balance.Equity,
                    TradableCash = Math.Max(cashAvailable - cashMinimum, 0),
                    TradeableEquity = Math.Max(balances.Balance.Equity - cashMinimum, 0)
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
            if(now.Hour > 12) { now = now.AddHours(9 - now.Hour); }
            await _rateLimiter.LimitTradierRate();
            var calendar = await GetResponse<TradierCalendarWrapper>(string.Format(CalendarPath, month.ToString("00"), year));
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
                        var previousCalendar = now.Month > 1 ? await GetResponse<TradierCalendarWrapper>(string.Format(CalendarPath, (now.Month - 1).ToString("00"), now.Year)) :
                                                               await GetResponse<TradierCalendarWrapper>(string.Format(CalendarPath, 12, now.Year - 1));
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
            var postOrder = TradierPostOrder.Buy(symbol.Replace('.','/'), roundedQty, limit);
            try
            {
                var response = await PostOrder(OrdersPath, postOrder);
                if (response.OrderStatus == TradierPostOrderResponse.SUCCESS_ORDERSTATUS)
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
            var postOrder = TradierPostOrder.Sell(symbol.Replace('.', '/'), roundedQty, limit);
            try
            {
                var response = await PostOrder(OrdersPath, postOrder);
                if (response.OrderStatus == TradierPostOrderResponse.SUCCESS_ORDERSTATUS)
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
            var jsonBars = await GetResponse<TradierHistoryBars>(string.Format(HistoryBarPath, symbol.Replace('.', '/'), from.ToString("yyyy-MM-dd"), _lastMarketDay.TradingTimeClose.ToString("yyyy-MM-dd")));
            return jsonBars?.History?.Quotes?.Where(b => b.Close > 0 && b.Open > 0 && b.Low > 0 && b.High > 0).Select(b => new HistoryBar
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
            var result = await GetResponse<TradierOrdersWrapper>($"{OrdersPath}?includeTags=true");
            var orders = result?.TradierOrders?.Orders?.Where(o => o.OrderStatus == TradierOrder.FILLED_ORDERSTATUS);
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
                OrderSide = o.OrderSide == TradierOrder.BUY_ORDERSIDE ? OrderSide.Buy : (o.OrderSide == TradierOrder.SELL_ORDERSIDE ? OrderSide.Sell : throw new Exception("Unknown order side")),
                Symbol = o.Symbol.Replace('/','.'),
                OrderStatus = OrderStatus.FILLED
            }).ToArray() ?? Enumerable.Empty<BrokerOrder>();
        }

        public async Task<BrokerOrder> GetOrder(string orderId)
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            var result = await GetResponse<TradierOrderWrapper>(string.Format(OrderPath, orderId));
            if(result?.Order != null)
            {
                var order = result.Order;
                var brokerOrder = new BrokerOrder
                {
                    BrokerOrderId = order.Id.ToString(),
                    ClientOrderId = order.Tag,
                    OrderSide = order.OrderSide == TradierOrder.BUY_ORDERSIDE ? OrderSide.Buy : (order.OrderSide == TradierOrder.SELL_ORDERSIDE ? OrderSide.Sell : throw new Exception("Unknown order side")),
                    Symbol = order.Symbol.Replace('/', '.')
                };
                if (order.OrderStatus == TradierOrder.FILLED_ORDERSTATUS || order.OrderStatus == TradierOrder.PARTIALLYFILLED_ORDERSTATUS)
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
            var result = await GetResponse<TradierOrdersWrapper>($"{OrdersPath}?includeTags=true");
            var orders = result?.TradierOrders?.Orders?.Where(o => o.OrderStatus == TradierOrder.OPEN_ORDERSTATUS || o.OrderStatus == TradierOrder.PENDING_ORDERSTATUS || o.OrderStatus == TradierOrder.PARTIALLYFILLED_ORDERSTATUS);
            return orders?.Select(o => new BrokerOrder
            {
                BrokerOrderId = o.Id.ToString(),
                ClientOrderId = o.Tag,
                OrderSide = o.OrderSide == TradierOrder.BUY_ORDERSIDE ? OrderSide.Buy : (o.OrderSide == TradierOrder.SELL_ORDERSIDE ? OrderSide.Sell : throw new Exception("Unknown order side")),
                Symbol = o.Symbol.Replace('/', '.')
            }).ToArray() ?? Enumerable.Empty<BrokerOrder>();
        }

        private OrderStatus GetBrokerOrderStatus(TradierOrder order)
        {
            if (order.OrderStatus == TradierOrder.FILLED_ORDERSTATUS) return OrderStatus.FILLED;
            else if (order.OrderStatus == TradierOrder.PARTIALLYFILLED_ORDERSTATUS) return OrderStatus.PARTIALLY_FILLED;
            else return OrderStatus.NOT_FILLED;
        }

        public async Task<Quote> GetLastTrade(string symbol)
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            var response = await GetResponse<TradierQuotesWrapper>(string.Format(QuotesPath, symbol.Replace('.', '/')));
            var quote = response?.TradierQuotes?.Quotes?.FirstOrDefault();
            if(quote == null)
            {
                throw new NullReferenceException($"Price information not found for symbol {symbol}!!");
            }
            return new Quote
            {
                Price = quote.Price,
                Size = quote.Size,
                Symbol = symbol,
                TradeTime = quote.TradeTime,
                TradeTimeMilliseconds = quote.TradeTimeMilliseconds
            };
        }
        public async Task<IEnumerable<Quote>> GetLastTrades(params string[] symbols)
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            var response = await GetResponse<TradierQuotesWrapper>(string.Format(QuotesPath, string.Join(',', symbols.Select(s => s.Replace('.', '/')))));
            var quotes = response?.TradierQuotes?.Quotes;
            if (quotes == null)
            {
                throw new NullReferenceException($"Price information not found for symbols {string.Join(',', symbols)}!!");
            }
            return quotes.Select(quote => new Quote
            {
                Price = quote.Price,
                Size = quote.Size,
                Symbol = quote.Symbol.Replace('/', '.'),
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
                var calendar = await GetResponse<TradierCalendarWrapper>(string.Format(CalendarPath, month.Value.ToString("00"), year));
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
                    var calendar = await GetResponse<TradierCalendarWrapper>(string.Format(CalendarPath, i.ToString("00"), year));
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
            var jmodelPositions = (await GetResponse<TradierPositionsWrapper>(PositionsPath))?.TradierPositions?.Positions;
            if(jmodelPositions != null && jmodelPositions.Length > 0)
            {
                var positions = new List<Position>();
                foreach(var jposition in jmodelPositions)
                {
                    jposition.Symbol = jposition.Symbol.Replace('/', '.');
                }
                var quotes = await GetLastTrades(jmodelPositions.Select(p => p.Symbol).ToArray());
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
                        AverageEntryPrice = jposition.Quantity > 0 ? (jposition.CostBasis / jposition.Quantity) : null,
                        UnrealizedProfitLoss = 0,
                        UnrealizedProfitLossPercent = 0
                    };
                    position.UnrealizedProfitLoss = position.MarketValue.HasValue && position.CostBasis > 0 ? position.MarketValue - position.CostBasis : null;
                    position.UnrealizedProfitLossPercent = position.CostBasis > 0 && position.UnrealizedProfitLoss.HasValue ? (position.UnrealizedProfitLoss / position.CostBasis) : null;
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
                var tradierSymbol = symbol.Replace('.', '/');
                var securities = (await GetResponse<TradierSecuritiesWrapper>(string.Format(LookupPath, tradierSymbol)))?.TradierSecurities?.Securities;
                if (securities != null && securities.Any())
                {
                    var security = securities.FirstOrDefault(s => s.Symbol == tradierSymbol);
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
        public async Task<(Dictionary<string, List<AccountHistoryEvent>> trades, double dividends)> GetAccountHistory()
        {
            await Ready();
            await _rateLimiter.LimitTradierRate();
            var historyEvents = new Dictionary<string, List<AccountHistoryEvent>>();
            var dividends = 0.0;
            for (var i = 1; i < 100; i++)
            {
                var history = await GetResponse<TradierAccountHistory>(string.Format(AccountHistoryPath, i));
                var eventsPage = history.History?.Events;
                if (eventsPage != null && eventsPage.Any())
                {
                    foreach (var historyEvent in eventsPage)
                    {
                        if (historyEvent.TypeStr == TradierAccountHistoryEvent.TradeType)
                        {
                            if (historyEvents.TryGetValue(historyEvent.Details.Symbol, out var events))
                            {
                                events.Add(historyEvent.ToHistoryEvent());
                            }
                            else
                            {
                                historyEvents.Add(historyEvent.Details.Symbol, new List<AccountHistoryEvent>(new[] { historyEvent.ToHistoryEvent() }));
                            }
                        }
                        else if (historyEvent.TypeStr == TradierAccountHistoryEvent.DividendType)
                        {
                            dividends += historyEvent.Amount;
                        }
                    }
                }
                else
                {
                    break;
                }
            }
            return (historyEvents, dividends);
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
                var json = await GetResponse<JToken>(string.Format(FinancialsPath, symbol.Replace('.', '/')));
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
                using var response = await client.GetAsync(string.Format(FinancialsPath, symbol.Replace('.', '/')), _appCancellation.Token);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(_appCancellation.Token);
                return json;
            }
        }
        public async Task AccountHistoryTest(string[] ignoreSymbols = null)
        {
            _tradierDataClient.BaseAddress = new Uri($"https://{PRODUCTION_URL}");
            _tradierDataClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _tradierDataClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_configuration[$"tradier_token:Production"]}");
            var profile = (await GetResponse<TradierProfileWrapper>(ProfilePath)).Profile;
            _account = new Account
            {
                AccountId = profile.Accounts.FirstOrDefault(a => a.Classification == "individual").AccountNumber
            };
            _startTask = Task.CompletedTask;
            var (historyEvents, dividends) = await GetAccountHistory();
            var totalRealizedProfits = new List<(double cost, double profitPerc)>();
            var totalUnRealizedProfits = new List<(double cost, double profitPerc)>();
            foreach (var historyEvent in historyEvents)
            {
                var events = historyEvent.Value.OrderBy(e => e.Date).ToArray();
                var eventQueue = new Queue<double>();
                var profits = new List<(double cost, double profitPerc)>();
                for(var i = 0; i < events.Length; i++)
                {
                    if(events[i].Amount < 0)
                    {
                        if (i == 0 || events[i - 1].Date.CompareTo(events[i].Date) != 0 || events[i-1].Amount < 0)
                        {
                            eventQueue.EnqueueEventDetails(events[i]);
                        }
                    }
                    else if (events[i].Amount > 0)
                    {
                        var sellPrice = events[i].Price;
                        for(var qtySold = Math.Abs(events[i].Qty); qtySold > 0; qtySold--)
                        {
                            if(eventQueue.Any())
                            {
                                var cost = eventQueue.Dequeue();
                                profits.Add((cost, (sellPrice / cost) - 1));
                            }
                        }
                    }
                }
                totalRealizedProfits.Add(TotalProfit(profits));
            }
            var (totalRealizedCost, totalRealized) = TotalProfit(totalRealizedProfits);
            _logger.LogInformation($"Total Realized Cost: {totalRealizedCost:C2} Total Realized Profit: {totalRealized * 100}%");
            var positions = await GetPositions();
            foreach (var position in positions)
            {
                if (position.UnrealizedProfitLossPercent.HasValue && (ignoreSymbols == null || !ignoreSymbols.Any(s => s == position.Symbol)))
                {
                    totalUnRealizedProfits.Add((position.CostBasis, position.UnrealizedProfitLossPercent.Value));
                }
            }
            var (totalUnrealizedCost, totalUnrealized) = TotalProfit(totalUnRealizedProfits);
            _logger.LogInformation($"Total Unrealized Cost: {totalUnrealizedCost:C2} Total Unrealized Profit: {totalUnrealized * 100}%");
            var (totalCost, totalProfit) = TotalProfit(new List<(double cost, double profitPerc)>(new[]
            {
                (totalRealizedCost, totalRealized),
                (totalUnrealizedCost, totalUnrealized)
            }));
            _logger.LogInformation($"Total Cost Basis: {totalCost:C2} Total Profit: {totalProfit * 100:0.0000}% Dividends: {dividends:C2}");
        }
        private static (double cost, double profitPerc) TotalProfit(List<(double cost, double profitPerc)> profits)
        {
            if (!profits.Any()) { return (0, 0); }
            var totalCost = 0.0;
            var numerator = 0.0;
            foreach (var profit in profits)
            {
                totalCost += profit.cost;
                numerator += profit.cost * profit.profitPerc;
            }
            return (totalCost, numerator / totalCost);
        }
#endif
        private async Task<T> GetResponse<T>(string path)
        {
            using var response = await _tradierDataClient.GetAsync(path, _appCancellation.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(_appCancellation.Token);
            var objResponse = JsonConvert.DeserializeObject<T>(json);
            if (objResponse == null)
            {
                _logger.LogError($"Deserialization of response failed. Content: {json}");
            }
            return objResponse;
        }
        private async Task<TradierPostOrderResponse> PostOrder(string path, TradierPostOrder order)
        {
            using var response = await _tradierOrderClient.PostAsync(path, order.HttpContent(), _appCancellation.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(_appCancellation.Token);
            var objResponse = JsonConvert.DeserializeObject<TradierPostOrderResponseWrapper>(json).Order;
            if(objResponse != null)
            {
                return objResponse;
            }
            _logger.LogError($"Deserialization of order response failed. Content: {json}");
            return new TradierPostOrderResponse
            {
                OrderStatus = "Rejected"
            };
        }

    }
}
