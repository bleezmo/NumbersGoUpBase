using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NumbersGoUp.Models;
using NumbersGoUp.Services;
using NumbersGoUp.Utils;
using NumbersGoUpBase.JsonModels;
using NumbersGoUpBase.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace NumbersGoUpBase.Services
{
    public class SchwabService : IBrokerService
    {
        private const string CASH_MIN = "CashMinimum";
        private const string CASH_PERC = "CashPerc";

        private static readonly JsonSerializerOptions jsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly ISchwabAccessTokenService _accessTokenService;
        private readonly HttpClient _schwabClient;
        private readonly ILogger<SchwabService> _logger;
        private readonly IAppCancellation _appCancellation;
        private readonly RateLimiter _rateLimiter;
        private readonly IConfiguration _configuration;
        private Task _startTask;
        private SchwabAccountDetails _account;
        private string _accountHashValue;
        private double _cashMinimum = 0;
        private MarketDay _marketDay;
        private MarketDay _lastMarketDay;
        private static readonly SemaphoreSlim _taskSem = new SemaphoreSlim(1, 1);

        public SchwabService(ISchwabAccessTokenService accessTokenService, ILogger<SchwabService> logger, IAppCancellation appCancellation, RateLimiter rateLimiter, IHttpClientFactory httpClientFactory, IConfiguration configuration) 
        { 
            _accessTokenService = accessTokenService;
            _schwabClient = httpClientFactory.CreateClient();
            _logger = logger;
            _appCancellation = appCancellation;
            _rateLimiter = rateLimiter;
            _configuration = configuration;
        }
        private async Task Init()
        {
            var accessToken = await _accessTokenService.GetAccessToken();
            if (accessToken == null)
            {
                _logger.LogError("Access token unavailable. Shutting down.");
                await _appCancellation.Shutdown();
            }
            _schwabClient.BaseAddress = new Uri(SchwabConfig.BaseUrl);
            _schwabClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _schwabClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            await LoadMarketDays();
            if (_marketDay == null) { _logger.LogError("Unable to retrieve current market day"); }
            else { _logger.LogInformation($"Using current market day {_marketDay.Date.ToString("yyyy-MM-dd HH:mm:ss")}"); }
            if (_lastMarketDay == null) { _logger.LogError("Unable to retrieve previous market day"); }
            else { _logger.LogInformation($"Using previous market day {_lastMarketDay.Date.ToString("yyyy-MM-dd HH:mm:ss")}"); }
            var accounts = await GetResponse<IEnumerable<SchwabAccount>>(SchwabConfig.AccountsEndpoint);
            var accountNumber = _configuration["SchwabAccountNumber"];
            _accountHashValue = accounts.FirstOrDefault(a => accountNumber.Contains(a.AccountNumber))?.HashValue;
            if (_accountHashValue == null) { 
                _logger.LogError("Could not find account!");
                await _appCancellation.Shutdown();
            }
            var accountWrapper = await GetResponse<SchwabAccountWrapper>(string.Format(SchwabConfig.AccountDetailsEndpoint, _accountHashValue));
            _account = accountWrapper.SecuritiesAccount;
            if(_account == null) { 
                _logger.LogError("Account retrieval failed!");
                await _appCancellation.Shutdown();
            }
            double.TryParse(_configuration[CASH_MIN], out var _cashMinimum);
            if (double.TryParse(_configuration[CASH_PERC], out var cashPerc))
            {
                _cashMinimum = Math.Max(_cashMinimum, cashPerc.DoubleReduce(1, 0) * _account.CurrentBalances.LiquidationValue);
            }
        }
        private async Task LoadMarketDays()
        {
            SchwabMarket nextOpen = null, lastOpen = null;
            var today = DateTime.UtcNow;
            for(var i = 0; i < 10; i++)
            {
                var market = await LoadMarketDay(today.AddDays(i));
                if(market != null)
                {
                    nextOpen = market;
                    break;
                }
            }
            if (nextOpen != null)
            {
                _marketDay = new MarketDay
                {
                    TradingTimeOpen = DateTime.Parse(nextOpen.Start),
                    TradingTimeClose = DateTime.Parse(nextOpen.End)
                };
            }
            for (var i = -1; i > -10; i--)
            {
                var market = await LoadMarketDay(today.AddDays(i));
                if (market != null)
                {
                    lastOpen = market;
                    break;
                }
            }
            if (lastOpen != null)
            {
                _lastMarketDay = new MarketDay
                {
                    TradingTimeOpen = DateTime.Parse(lastOpen.Start),
                    TradingTimeClose = DateTime.Parse(lastOpen.End)
                };
            }
        }
        private async Task<SchwabMarket> LoadMarketDay(DateTime day)
        {
            await _rateLimiter.LimitSchwabRate();
            using var response = await _schwabClient.GetAsync(string.Format(SchwabConfig.MarketHoursEndpoint, day.ToString("yyyy-MM-dd")), _appCancellation.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(_appCancellation.Token);
            var node = JsonNode.Parse(json)["equity"];
            node = node["EQ"] ?? node["equity"];
            var market = node.Deserialize<SchwabMarketsWrapper>(jsonSerializerOptions);
            if (market != null && market.IsOpen)
            {
                return market.SessionHours.RegularMarket.FirstOrDefault();
            }
            return null;
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
            await _rateLimiter.LimitSchwabRate();
            var history = await GetResponse<SchwabHistory>(string.Format(SchwabConfig.PriceHistoryEndpoint, symbol, new DateTimeOffset(from).ToUnixTimeMilliseconds()));
            if(history == null || history.Empty)
            {
                return Enumerable.Empty<HistoryBar>();
            }
            return history.Candles.Select(bar => {
                if (bar.IsInvalid()) { return null; }
                return new HistoryBar
                {
                    OpenPrice = bar.Open.Value,
                    ClosePrice = bar.Close.Value,
                    HighPrice = bar.High.Value,
                    LowPrice = bar.Low.Value,
                    Volume = bar.Volume,
                    Symbol = symbol,
                    BarDay = DateTimeOffset.FromUnixTimeMilliseconds(bar.Datetime.Value).UtcDateTime,
                    BarDayMilliseconds = bar.Datetime.Value
                };
            }).Where(b => b != null).OrderBy(b => b.BarDayMilliseconds);
        }

        public async Task<Quote> GetLastTrade(string symbol)
        {
            await Ready();
            await _rateLimiter.LimitSchwabRate();
            using var response = await _schwabClient.GetAsync(string.Format(SchwabConfig.QuoteEndpoint, symbol), _appCancellation.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(_appCancellation.Token);
            var node = JsonNode.Parse(json);
            var quote = node[symbol]["quote"].Deserialize<SchwabQuote>(jsonSerializerOptions);
            if (quote.IsInvalid()) { return null; }
            return new Quote
            {
                Price = quote.LastPrice.Value,
                Size = quote.LastSize,
                Symbol = symbol,
                TradeTimeMilliseconds = quote.TradeTime.Value,
                TradeTime = DateTimeOffset.FromUnixTimeMilliseconds(quote.TradeTime.Value).UtcDateTime
            };
        }
        public async Task<IEnumerable<Quote>> GetLastTrades(string[] symbols)
        {
            await Ready();
            await _rateLimiter.LimitSchwabRate();
            using var response = await _schwabClient.GetAsync(string.Format(SchwabConfig.QuoteEndpoint, string.Join(',', symbols)), _appCancellation.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(_appCancellation.Token);
            var node = JsonNode.Parse(json);
            var quotes = new List<Quote>();
            for(var i = 0; i < symbols.Length; i++)
            {
                var symbol = symbols[i];
                var quote = node[symbol]["quote"].Deserialize<SchwabQuote>(jsonSerializerOptions);
                if(!quote.IsInvalid())
                {
                    quotes.Add(new Quote
                    {
                        Price = quote.LastPrice.Value,
                        Size = quote.LastSize,
                        Symbol = symbol,
                        TradeTimeMilliseconds = quote.TradeTime.Value,
                        TradeTime = DateTimeOffset.FromUnixTimeMilliseconds(quote.TradeTime.Value).UtcDateTime
                    });
                }
            }
            return quotes;
        }

        public async Task<IEnumerable<Position>> GetPositions()
        {
            await Ready();
            await _rateLimiter.LimitSchwabRate();
            if(_account.Positions == null) { return  Enumerable.Empty<Position>(); }
            var isInvalid = _account.Positions.Any(p => p.IsInvalid());
            if (isInvalid)
            {
                _logger.LogError("Invalid position found. Shutting down to avoid inaccurate calculations");
                await _appCancellation.Shutdown();
                return null;
            }
            var quotes = await GetLastTrades(_account.Positions.Select(position => position.Instrument.Symbol).ToArray());
            return _account.Positions.Select(position => 
            {
                var costBasis = position.AveragePrice * position.LongQuantity;
                var quote = quotes.First(q => q.Symbol == position.Instrument.Symbol);
                var marketValue = position.MarketValue.HasValue ? position.MarketValue.Value : (quote != null ? (quote.Price * position.LongQuantity) : null);
                return new Position
                {
                    AssetCurrentPrice = quote?.Price,
                    AssetLastPrice = quote?.Price,
                    Symbol = position.Instrument.Symbol,
                    AverageEntryPrice = position.AveragePrice,
                    CostBasis = costBasis.Value,
                    MarketValue = marketValue,
                    Quantity = position.LongQuantity.Value,
                    UnrealizedProfitLoss = marketValue.HasValue ? (marketValue - costBasis) : null,
                    UnrealizedProfitLossPercent = marketValue.HasValue ? ((marketValue - costBasis) / costBasis) : null,
                };
            });
        }
        public async Task<Account> GetAccount()
        {
            await Ready();
            return new Account
            {
                AccountId = _account.AccountNumber,
                Balance = new Balance
                {
                    BuyingPower = 0,
                    LastEquity = _account.CurrentBalances.LiquidationValue,
                    TradableCash = Math.Max(_account.CurrentBalances.CashBalance - _cashMinimum, 0),
                    TradeableEquity = Math.Max(_account.CurrentBalances.LiquidationValue - _cashMinimum, 0)
                }
            };
        }

        public async Task<Position> GetPosition(string symbol)
        {
            var positions = await GetPositions();
            return positions.FirstOrDefault(position => position.Symbol == symbol);
        }

        public async Task<BrokerOrder> Sell(string symbol, double qty, double? limit = null)
        {
            await Ready();
            await _rateLimiter.LimitSchwabRate();
            var order = limit.HasValue ? SchwabOrder.DefaultLimitSell(Math.Round(limit.Value, 2, MidpointRounding.AwayFromZero), qty, symbol) : SchwabOrder.DefaultMarketSell(qty, symbol);
            var response = await _schwabClient.PostAsJsonAsync(string.Format(SchwabConfig.OrderEndpoint, _accountHashValue), order, jsonSerializerOptions, _appCancellation.Token);
            response.EnsureSuccessStatusCode();
            var location = response.Headers.Location;
            var responseOrder = await GetResponse<SchwabOrder>(location.AbsolutePath);
            var executionLeg = responseOrder.OrderActivityCollection?.FirstOrDefault()?.ExecutionLegs?.FirstOrDefault();
            return new BrokerOrder
            {
                BrokerOrderId = responseOrder.OrderId.ToString(),
                OrderSide = OrderSide.Sell,
                Symbol = symbol
            };
        }

        public async Task<BrokerOrder> Buy(string symbol, double qty, double? limit = null)
        {
            await Ready();
            await _rateLimiter.LimitSchwabRate();
            var order = limit.HasValue ? SchwabOrder.DefaultLimitBuy(Math.Round(limit.Value, 2, MidpointRounding.AwayFromZero), qty, symbol) : SchwabOrder.DefaultMarketBuy(qty, symbol);
            var response = await _schwabClient.PostAsJsonAsync(string.Format(SchwabConfig.OrderEndpoint, _accountHashValue), order, jsonSerializerOptions, _appCancellation.Token);
            response.EnsureSuccessStatusCode();
            var location = response.Headers.Location;
            var responseOrder = await GetResponse<SchwabOrder>(location.AbsolutePath);
            var executionLeg = responseOrder.OrderActivityCollection?.FirstOrDefault()?.ExecutionLegs?.FirstOrDefault();
            return new BrokerOrder
            {
                BrokerOrderId = responseOrder.OrderId.ToString(),
                OrderSide = OrderSide.Buy,
                Symbol = symbol
            };
        }

        public async Task<IEnumerable<BrokerOrder>> GetOpenOrders()
        {
            await Ready();
            await _rateLimiter.LimitSchwabRate();
            const string isodateformat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            var now = DateTime.UtcNow;
            var from = now.AddHours(-24);
            var queryParams = $"fromEnteredTime={from.ToString(isodateformat)}&toEnteredTime={now.ToString(isodateformat)}";
            var orders = await GetResponse<IEnumerable<SchwabOrder>>($"{string.Format(SchwabConfig.OrderEndpoint, _accountHashValue)}?{queryParams}");
            return orders.Where(o => new[] { 
                SchwabOrderStatus.ACCEPTED,
                SchwabOrderStatus.NEW,
                SchwabOrderStatus.QUEUED,
                SchwabOrderStatus.WORKING,
                SchwabOrderStatus.PENDING_ACTIVATION
            }.Any(status => o.Status == status)).Select(o =>
            {
                var (brokerOrder, error) = o.ToBrokerOrder();
                if (brokerOrder == null)
                {
                    _logger.LogError(error);
                }
                return brokerOrder;
            }).Where(o => o != null);
        }

        public async Task<IEnumerable<BrokerOrder>> GetClosedOrders(DateTime? from = null)
        {
            await Ready();
            await _rateLimiter.LimitSchwabRate();
            const string isodateformat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            var now = DateTime.UtcNow;
            from = from ?? now.AddYears(-1);
            var queryParams = $"fromEnteredTime={from.Value.ToString(isodateformat)}&toEnteredTime={now.ToString(isodateformat)}&status=FILLED";
            var orders = await GetResponse<IEnumerable<SchwabOrder>>($"{string.Format(SchwabConfig.OrderEndpoint, _accountHashValue)}?{queryParams}");
            return orders.Select(o =>
            {
                var (brokerOrder, error) = o.ToBrokerOrder();
                if (brokerOrder == null)
                {
                    _logger.LogError(error);
                }
                return brokerOrder;
            }).Where(o => o != null);
        }

        public async Task<BrokerOrder> GetOrder(string brokerOrderId)
        {
            await Ready();
            await _rateLimiter.LimitSchwabRate();
            var order = await GetResponse<SchwabOrder>($"{string.Format(SchwabConfig.OrderEndpoint, _accountHashValue)}/{brokerOrderId}");
            var (brokerOrder, error) = order.ToBrokerOrder();
            if(brokerOrder == null)
            {
                _logger.LogError(error);
            }
            return brokerOrder;
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


        public async Task<Financials> GetFinancials(string symbol)
        {
            await Ready();
            await _rateLimiter.LimitSchwabRate();
            var fundamentals = await GetResponse<SchwabFundamentals>(string.Format(SchwabConfig.FinancialsEndpoint, symbol));
            return fundamentals.Instruments.Select(i => new Financials
            {
                EBIT = i.Fundamental.EpsTTM * i.Fundamental.SharesOutstanding,
                EPS = i.Fundamental.EpsTTM
            }).FirstOrDefault();
        }

        public async Task<IEnumerable<SchwabFundamental>> GetSchwabFinancials(string[] symbols)
        {
            await Ready();
            await _rateLimiter.LimitSchwabRate();
            var fundamentals = await GetResponse<SchwabFundamentals>(string.Format(SchwabConfig.FinancialsEndpoint, string.Join(',', symbols)));
            return fundamentals.Instruments.Select(i => i.Fundamental);
        }

        public Task<IEnumerable<MarketDay>> GetMarketDays(int year, int? month = null)
        {
            throw new NotImplementedException();
        }

        public async Task<(Dictionary<string, List<AccountHistoryEvent>> trades, double dividends)> GetAccountHistory()
        {
            await Ready();
            await _rateLimiter.LimitSchwabRate();
            const string isodateformat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            var end = DateTime.UtcNow;
            var start = end.AddMonths(-1);
            var response = await _schwabClient.GetAsync(string.Format(SchwabConfig.TransactionHistoryTradeEndpoint, _accountHashValue, start.ToString(isodateformat), end.ToString(isodateformat)), _appCancellation.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return (null, 0);
        }

        public void Dispose()
        {
            _schwabClient?.Dispose();
        }
        private async Task<T> GetResponse<T>(string path, params string[] keyPath)
        {
            using var response = await _schwabClient.GetAsync(path, _appCancellation.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(_appCancellation.Token);
            T objResponse;
            if(keyPath != null)
            {
                var node = JsonNode.Parse(json);
                foreach (var key in keyPath)
                {
                    if (node == null)
                    {
                        _logger.LogError($"Failed to navigate json for path: {path}");
                        return default;
                    }
                    node = node[key];
                }
                objResponse = node.Deserialize<T>(jsonSerializerOptions);
            }
            else
            {
                objResponse = JsonSerializer.Deserialize<T>(json, jsonSerializerOptions);
            }
            if (objResponse == null)
            {
                _logger.LogError($"Deserialization of response failed for path {path}");
            }
            return objResponse;
        }
    }
}
