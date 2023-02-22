using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NumbersGoUp.Models;
using NumbersGoUp.Utils;

namespace NumbersGoUp.Services
{
    public class TraderService
    {
        public const double MAX_SECURITY_BUY = 0.02;
        public const double MAX_SECURITY_SELL = MAX_SECURITY_BUY * 2 / 3;
        public const double MAX_DAILY_BUY = 0.1;
        public const double MAX_DAILY_SELL = 0.1;
        public const double MULTIPLIER_BUY_THRESHOLD = 0.4;
        public const double MULTIPLIER_SELL_THRESHOLD = 0.4;
        public const double MAX_COOLDOWN_DAYS = 10;
        public const bool USE_MARGIN = false;

        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TraderService> _logger;
        private readonly IBrokerService _brokerService;
        private readonly PredicterService _predicterService;
        private readonly TickerService _tickerService;
        private readonly DataService _dataService;
        private readonly string _environmentName;
        private readonly IStocksContextFactory _contextFactory;
        private Account _account;
        private double _cashEquityRatio;

        public TraderService(IAppCancellation appCancellation, IHostEnvironment environment, ILogger<TraderService> logger, TickerService tickerService, 
                             IBrokerService brokerService, PredicterService predicterService, DataService dataService, IStocksContextFactory contextFactory)
        {
            _appCancellation = appCancellation;
            _logger = logger;
            _brokerService = brokerService;
            _predicterService = predicterService;
            _tickerService = tickerService;
            _dataService = dataService;
            _environmentName = environment.EnvironmentName;
            _contextFactory = contextFactory;
        }
        public async Task Run()
        {
            _logger.LogInformation($"{nameof(TraderService)} awaiting broker service");
            await _brokerService.Ready();
            _logger.LogInformation("Running Trader");
            try
            {
                var marketOpen = await _brokerService.GetMarketOpen();
                if (DateTime.Now.CompareTo(marketOpen.AddHours(-4)) > 0)
                {
                    _account = await _brokerService.GetAccount();
                    _logger.LogInformation("Running previous-day metrics");
                    await PreviousDayTradeMetrics();
                    var equity = _account.Balance.LastEquity;
                    var cash = _account.Balance.TradableCash;
                    if (equity == 0)
                    {
                        _logger.LogError("Error retrieving equity value!");
                    }
                    else
                    {
                        if (cash < 0)
                        {
                            _logger.LogError("Negative cash balance!");
                        }
                        _cashEquityRatio = Math.Max(cash / equity, 0);
                        _logger.LogInformation($"Using Cash-Equity Ratio: {_cashEquityRatio}");
                        _logger.LogInformation("Running buy orders");
                        await Buy();
                        _logger.LogInformation("Running sell orders");
                        await Sell();
                        _logger.LogInformation("Running order executions");
                        await ExecuteOrders();
                    }
                    _logger.LogInformation("Cleaning up");
                    await CleanUp();
                }
                else
                {
                    _logger.LogInformation("To early for trading.");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "error running trades");
                throw;
            }
            finally
            {
                _logger.LogInformation("Trades Completed");
            }
        }
        public async Task Buy()
        {
            var now = DateTime.UtcNow;
            var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            List<DbOrder> currentOrders;
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                currentOrders = await stocksContext.Orders.Where(o => o.Account == _account.AccountId && o.TimeLocalMilliseconds > dayStart).ToListAsync(_appCancellation.Token);
            }
            var tickers = await _tickerService.GetTickers();
            var positions = await _brokerService.GetPositions();
            var tickerPositions = tickers.Select(t => new TickerPosition { Ticker = t, Position = positions.FirstOrDefault(p => t.Symbol == p.Symbol) }).ToArray();

            var buys = new List<BuySellState>();
            foreach (var tickerPosition in tickerPositions.Where(tp => !currentOrders.Any(o => o.Symbol == tp.Ticker.Symbol)))
            {
                try
                {
                    var ticker = tickerPosition.Ticker;
                    if (_appCancellation.IsCancellationRequested) break;
                    using (var stocksContext = _contextFactory.CreateDbContext())
                    {
                        var lastBuyOrder = await stocksContext.OrderHistories.Where(o => o.Account == _account.AccountId && o.Symbol == ticker.Symbol && o.NextBuy != null).OrderByDescending(o => o.TimeLocalMilliseconds).Take(1).FirstOrDefaultAsync(_appCancellation.Token);
                        if (lastBuyOrder != null && lastBuyOrder.NextBuy.Value.Date.CompareTo(now) > 0) continue;
                    }
                    var prediction = await _predicterService.BuyPredict(ticker.Symbol);
                    if (prediction.HasValue)
                    {
                        var lastBarMetric = await _dataService.GetLastMetric(ticker.Symbol);
                        if (lastBarMetric != null)
                        {
                            var buyMultiplier = prediction.Value;
                            var currentPrice = tickerPosition.Position?.AssetLastPrice != null ? tickerPosition.Position.AssetLastPrice.Value : (await _brokerService.GetLastTrade(ticker.Symbol)).Price;
                            var percProfit = tickerPosition.Position != null ? (tickerPosition.Position.UnrealizedProfitLossPercent != null ? tickerPosition.Position.UnrealizedProfitLossPercent.Value * 100 :
                                                                                                                                              (currentPrice - tickerPosition.Position.CostBasis) * 100 / tickerPosition.Position.CostBasis) : 0.0;
                            if (tickerPosition.Position != null)
                            {
                                buyMultiplier *= 1 - ((tickerPosition.Position.Quantity * currentPrice) / (_account.Balance.LastEquity * MaxTickerEquityPerc(ticker, lastBarMetric))).DoubleReduce(1, 0.25);
                            }
                            buyMultiplier = FinalBuyMultiplier(buyMultiplier);
                            if (buyMultiplier > MULTIPLIER_BUY_THRESHOLD)
                            {
                                buys.Add(new BuySellState
                                {
                                    BarMetric = lastBarMetric,
                                    Multiplier = buyMultiplier,
                                    TickerPosition = tickerPosition,
                                    ProfitLossPerc = percProfit
                                });
                            }
                        }
                    }
                }
                catch(Exception e)
                {
                    _logger.LogError(e, $"Error when processing buy for {tickerPosition.Ticker.Symbol}");
                }
            }
            var multiplierSum = 0.0;
            var filteredBuys = new List<BuySellState>();
            foreach (var buy in buys.OrderByDescending(priorityOrdering))
            {
                multiplierSum += buy.Multiplier * MAX_SECURITY_BUY;
                if (multiplierSum > MAX_DAILY_BUY) { break; }
                else { filteredBuys.Add(buy); }
            }
            //foreach (var buy in filteredBuys)
            //{
            //    buy.Multiplier *= 1 - multiplierSum.DoubleReduce(MAX_DAILY_BUY, MAX_SECURITY_BUY).Curve2(_cashEquityRatio.DoubleReduce(0.2, 0, 8, 2), 1.2);
            //}
            foreach (var buy in filteredBuys)
            {
                var limitCoeff = 1 - buy.Multiplier.Curve3(3);
                var limit = (limitCoeff * buy.BarMetric.HistoryBar.LowPrice) + ((1 - limitCoeff) * buy.BarMetric.HistoryBar.ClosePrice);
                await AddOrder(OrderSide.Buy, buy.BarMetric.Symbol, limit, buy.Multiplier);
                _logger.LogInformation($"Added buy order for {buy.BarMetric.Symbol} with multiplier {buy.Multiplier} at price {limit:C2}");
            }
        }
        public async Task Sell()
        {
            var now = DateTime.UtcNow;
            var positions = await _brokerService.GetPositions();
            var tickers = await _tickerService.GetTickers(positions.Select(p => p.Symbol).ToArray());
            var tickerPositions = positions.Select(p => new TickerPosition { Position = p, Ticker = tickers.FirstOrDefault(t => t.Symbol == p.Symbol) });

            var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            List<DbOrder> currentOrders;
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                currentOrders = await stocksContext.Orders.Where(o => o.TimeLocalMilliseconds > dayStart).OrderByDescending(o => o.TimeLocalMilliseconds).ToListAsync(_appCancellation.Token);
            }
            var tickerPositionsNP = tickerPositions.Where(tp => !currentOrders.Any(o => o.Symbol == tp.Position.Symbol)); //positions Not Processed
            var invalidatedTickers = tickerPositionsNP.Where(tp => tp.Ticker == null).Select(tp => tp.Position.Symbol);
            if (invalidatedTickers.Any())
            {
                _logger.LogError($"The following ticker symbols were not found in the database: {string.Join(',', invalidatedTickers)} Manual intervention required.");
            }
            var sells = new List<BuySellState>();
            foreach (var tickerPosition in tickerPositionsNP.Where(tp => tp.Ticker != null))
            {
                try
                {
                    var position = tickerPosition.Position;
                    var ticker = tickerPosition.Ticker;
                    using (var stocksContext = _contextFactory.CreateDbContext())
                    {
                        var lastSellOrder = await stocksContext.OrderHistories.Where(o => o.Account == _account.AccountId && o.Symbol == position.Symbol && o.NextSell != null).OrderByDescending(o => o.TimeLocalMilliseconds).Take(1).FirstOrDefaultAsync(_appCancellation.Token);
                        if (lastSellOrder != null && lastSellOrder.NextSell.Value.Date.CompareTo(now) > 0) continue;
                    }
                    var avgEntryPrice = position.AverageEntryPrice;
                    var predictionSell = await _predicterService.SellPredict(position.Symbol);
                    if (!predictionSell.HasValue) // if null comes back, means there's no info for symbol
                    {
                        _logger.LogWarning($"Invalid metrics for {position.Symbol}. Closing position.");
                        await _brokerService.ClosePositionAtMarket(position.Symbol);
                    }
                    else if (predictionSell.Value > 0)
                    {
                        var sellMultiplier = predictionSell.Value;
                        var lastBarMetric = await _dataService.GetLastMetric(position.Symbol);
                        var currentPrice = position.AssetLastPrice.HasValue ? position.AssetLastPrice.Value : (await _brokerService.GetLastTrade(position.Symbol)).Price;
                        var percProfit = position.UnrealizedProfitLossPercent.HasValue ? position.UnrealizedProfitLossPercent.Value * 100 : ((currentPrice - position.CostBasis) * 100 / position.CostBasis);

                        sellMultiplier += (1 - sellMultiplier) * sellMultiplier * ((tickerPosition.Position.Quantity * currentPrice) / (_account.Balance.LastEquity * MaxTickerEquityPerc(ticker, lastBarMetric))).DoubleReduce(1.5, 0.5);


                        if (percProfit < -5 && lastBarMetric.BarDay.Month == 12 && lastBarMetric.BarDay.Day > 10) //tax loss harvest
                        {
                            sellMultiplier += (1 - sellMultiplier) * sellMultiplier;
                        }
                        sellMultiplier = FinalSellMultiplier(sellMultiplier);
                        if (sellMultiplier > MULTIPLIER_SELL_THRESHOLD)
                        {
                            sells.Add(new BuySellState
                            {
                                BarMetric = lastBarMetric,
                                Multiplier = sellMultiplier,
                                TickerPosition = tickerPosition,
                                ProfitLossPerc = percProfit
                            });
                        }
                    }
                }
                catch(Exception e)
                {
                    _logger.LogError(e, $"Error when processing sell for {tickerPosition.Ticker.Symbol}");
                }
            }
            var multiplierSum = 0.0;
            var filteredSells = new List<BuySellState>();
            foreach (var sell in sells.OrderBy(priorityOrdering))
            {
                multiplierSum += sell.Multiplier * MAX_SECURITY_SELL;
                if (multiplierSum > MAX_DAILY_SELL) { break; }
                else { filteredSells.Add(sell); }
            }
            //foreach (var sell in filteredSells)
            //{
            //    sell.Multiplier *= 1 - multiplierSum.DoubleReduce(MAX_DAILY_SELL, MAX_SECURITY_SELL);
            //}
            foreach (var sell in filteredSells)
            {
                var limitCoeff = 1 - sell.Multiplier.Curve3(3);
                var limit = (limitCoeff * sell.BarMetric.HistoryBar.HighPrice) + ((1 - limitCoeff) * sell.BarMetric.HistoryBar.ClosePrice);
                await AddOrder(OrderSide.Sell, sell.BarMetric.Symbol, limit, sell.Multiplier);
                _logger.LogInformation($"Added sell order for {sell.BarMetric.Symbol} with multiplier {sell.Multiplier} at price {limit:C2}");
            }
        }
        private double priorityOrdering(BuySellState bss) => bss.TickerPosition.Ticker.PerformanceVector * bss.ProfitLossPerc.ZeroReduce(bss.TickerPosition.Ticker.ProfitLossAvg + bss.TickerPosition.Ticker.ProfitLossStDev, (bss.TickerPosition.Ticker.ProfitLossAvg + bss.TickerPosition.Ticker.ProfitLossStDev) * -1);
        private double FinalSellMultiplier(double sellMultiplier) => sellMultiplier.Curve1(_cashEquityRatio.DoubleReduce(0.5, 0, 5, 1));
        private double FinalBuyMultiplier(double buyMultiplier) => buyMultiplier.Curve3((1 - _cashEquityRatio).DoubleReduce(1, 0.7, 4, 2));
        private double MaxTickerEquityPerc(Ticker ticker, BarMetric lastBarMetric) => (0.5 * lastBarMetric.ProfitLossPerc.DoubleReduce(ticker.ProfitLossAvg, ticker.ProfitLossAvg - (ticker.ProfitLossStDev* 1.5))) + (0.5 * ticker.PerformanceVector.DoubleReduce(100, 0) * ticker.DividendYield.DoubleReduce(0.04, 0));

        private async Task AddOrder(OrderSide orderSide, string symbol, double targetPrice, double multiplier)
        {
            var now = DateTime.UtcNow;
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                var ticker = await stocksContext.Tickers.Where(t => t.Symbol == symbol).FirstOrDefaultAsync(_appCancellation.Token);
                var lastBuyOrder = await stocksContext.OrderHistories.Where(o => o.Account == _account.AccountId && o.Symbol == ticker.Symbol && o.Side == OrderSide.Buy).OrderByDescending(o => o.TimeLocalMilliseconds).Take(1).FirstOrDefaultAsync(_appCancellation.Token);
                stocksContext.Orders.Add(new DbOrder
                {
                    TickerId = ticker.Id,
                    Symbol = symbol,
                    TargetPrice = targetPrice,
                    Side = orderSide,
                    TimeLocal = now,
                    TimeLocalMilliseconds = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
                    Multiplier = multiplier,
                    DaysFromLastBuy = orderSide == OrderSide.Sell ? Convert.ToInt32(Math.Floor(now.Subtract(lastBuyOrder?.TimeLocal ?? now).TotalDays)) : 0,
                    Account = _account.AccountId
                });
                await stocksContext.SaveChangesAsync(_appCancellation.Token);
            }
        }

        #region Runner

        private async Task PreviousDayTradeMetrics()
        {
            using var stocksContext = _contextFactory.CreateDbContext();
            var orders = await GetPreviousTradingDayFilledOrders();
            foreach (var (order, brokerOrder) in orders)
            {
                var profitLossPerc = 0.0;
                var dayOfWeek = brokerOrder.FilledAt.Value.DayOfWeek;
                var daysToNextBuy = (1 - _cashEquityRatio.DoubleReduce(1, 0.2)) * MAX_COOLDOWN_DAYS; var daysToNextSell = _cashEquityRatio.DoubleReduce(0.2, 0) * MAX_COOLDOWN_DAYS;
                if (order != null && brokerOrder.OrderSide == OrderSide.Sell && order.AvgEntryPrice > 0)
                {
                    profitLossPerc = (brokerOrder.AverageFillPrice.Value - order.AvgEntryPrice) * 100 / order.AvgEntryPrice;
                    if (profitLossPerc < -5)
                    {
                        daysToNextBuy = 62;
                    }
                }
                var lastBuyOrder = await stocksContext.OrderHistories.Where(o => o.Account == _account.AccountId && o.Symbol == order.Symbol && o.NextBuy != null).OrderByDescending(o => o.TimeLocalMilliseconds).Take(1).FirstOrDefaultAsync(_appCancellation.Token);
                var lastSellOrder = await stocksContext.OrderHistories.Where(o => o.Account == _account.AccountId && o.Symbol == order.Symbol && o.NextSell != null).OrderByDescending(o => o.TimeLocalMilliseconds).Take(1).FirstOrDefaultAsync(_appCancellation.Token);
                if(lastBuyOrder != null && brokerOrder.FilledAt.Value.CompareTo(lastBuyOrder.NextBuy.Value) < 0)
                {
                    daysToNextBuy = Math.Max((int)Math.Ceiling(lastBuyOrder.NextBuy.Value.Subtract(brokerOrder.FilledAt.Value).TotalDays), daysToNextBuy);
                }
                if (lastSellOrder != null && brokerOrder.FilledAt.Value.CompareTo(lastSellOrder.NextSell.Value) < 0)
                {
                    daysToNextSell = Math.Max((int)Math.Ceiling(lastSellOrder.NextSell.Value.Subtract(brokerOrder.FilledAt.Value).TotalDays), daysToNextSell);
                }
                var historyOrder = new DbOrderHistory
                {
                    Symbol = brokerOrder.Symbol,
                    Side = brokerOrder.OrderSide,
                    AvgFillPrice = brokerOrder.AverageFillPrice.Value,
                    FillQty = brokerOrder.FilledQuantity,
                    TimeLocal = brokerOrder.FilledAt.Value.ToUniversalTime(),
                    TimeLocalMilliseconds = new DateTimeOffset(brokerOrder.FilledAt.Value).ToUnixTimeMilliseconds(),
                    NextBuy = brokerOrder.FilledAt.Value.AddDays(daysToNextBuy).ToUniversalTime(),
                    NextSell = brokerOrder.FilledAt.Value.AddDays(daysToNextSell).ToUniversalTime(),
                    ProfitLossPerc = profitLossPerc,
                    Account = order.Account,
                    BrokerOrderId = order.BrokerOrderId
                };
                stocksContext.OrderHistories.Add(historyOrder);
            }
            await stocksContext.SaveChangesAsync(_appCancellation.Token);
        }
        private async Task<IEnumerable<(DbOrder dbOrder, BrokerOrder brokerOrder)>> GetPreviousTradingDayFilledOrders()
        {
            var lastMarketDay = await _brokerService.GetLastMarketDay();
            var lastMarketDayMillis = new DateTimeOffset(lastMarketDay.Date).ToUnixTimeMilliseconds();
            DbOrder[] orders;
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                orders = await stocksContext.Orders.Where(o => o.TimeLocalMilliseconds > lastMarketDayMillis && o.Account == _account.AccountId).Include(o => o.Ticker).ToArrayAsync(_appCancellation.Token);
            }
            var brokerOrders = await Task.WhenAll(orders.Select<DbOrder, Task<(DbOrder dbOrder, BrokerOrder brokerOrder)>>(async o => (o, await _brokerService.GetOrder(o.BrokerOrderId))));
            return brokerOrders.Where(o => o.brokerOrder != null && o.brokerOrder.AverageFillPrice.HasValue && o.brokerOrder.FilledAt.HasValue);
        }
        private async Task CleanUp()
        {
            var now = DateTimeOffset.Now;
            if (now.DayOfWeek == DayOfWeek.Wednesday)
            {
                var cutoff = now.AddYears(-1).ToUnixTimeMilliseconds();
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var orderHistories = await stocksContext.OrderHistories.Where(oh => oh.TimeLocalMilliseconds < cutoff).ToArrayAsync(_appCancellation.Token);
                    if (orderHistories.Any())
                    {
                        stocksContext.OrderHistories.RemoveRange(orderHistories);
                        await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    }
                    var orders = await stocksContext.Orders.Where(oh => oh.TimeLocalMilliseconds < cutoff).ToArrayAsync(_appCancellation.Token);
                    if (orders.Any())
                    {
                        stocksContext.Orders.RemoveRange(orders);
                        await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    }
                }
            }
        }
        private async Task ExecuteOrders()
        {
            var cash = _account.Balance.TradableCash;
            var balance = cash;
            if (USE_MARGIN && _account.Balance.BuyingPower.HasValue && _account.Balance.BuyingPower.Value > (_account.Balance.TradableCash * 1.5))
            {
                balance = _account.Balance.TradableCash * 1.5;
            }
            else if (USE_MARGIN && _account.Balance.BuyingPower.HasValue)
            {
                balance = _account.Balance.BuyingPower.Value;
            }
            var now = DateTime.Now;
            var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            List<DbOrder> currentOrders;
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                currentOrders = await stocksContext.Orders.Where(o => o.Account == _account.AccountId && o.TimeLocalMilliseconds > dayStart).Include(o => o.Ticker).ToListAsync(_appCancellation.Token);
            }
            currentOrders = currentOrders.Where(o => string.IsNullOrWhiteSpace(o.BrokerOrderId))
                                         .Where(o1 => (o1.Side == OrderSide.Buy && !currentOrders.Any(o2 => o2.Side == OrderSide.Sell && o2.Symbol == o1.Symbol)) ||
                                                      (o1.Side == OrderSide.Sell && !currentOrders.Any(o2 => o2.Side == OrderSide.Buy && o2.Symbol == o1.Symbol))).ToList();

            var buyOrders = currentOrders.Where(o => o.Side == OrderSide.Buy).OrderByDescending(o => o.Ticker.PerformanceVector).ToArray();
            var sellOrders = currentOrders.Where(o => o.Side == OrderSide.Sell).ToArray();

            //var avgBuyMultiplier = buyOrders.Any() ? buyOrders.Select(o => o.Multiplier).Average() : 0.0;
            var remainingBuyAmount = Math.Min(_account.Balance.LastEquity * MAX_DAILY_BUY * _cashEquityRatio.DoubleReduce(0.3,0).Curve4(1), balance);

            remainingBuyAmount -= currentOrders.Select(o => o.Side == OrderSide.Buy ? o.AppliedAmt : 0).Sum();
            _logger.LogInformation($"Starting balance {balance:C2} and remaining buy amount {remainingBuyAmount:C2}");
            await ExecuteSells(sellOrders);
            await ExecuteBuys(buyOrders, remainingBuyAmount);
        }
        private async Task ExecuteSells(DbOrder[] orders)
        {
            var equity = _account.Balance.LastEquity;
            var positions = await _brokerService.GetPositions();
            foreach (var order in orders)
            {
                var position = positions.FirstOrDefault(p => p.Symbol == order.Symbol);
                if (position == null)
                {
                    _logger.LogError($"No position found for {order.Symbol}!!! Can't Sell!!!");
                    continue;
                }
                var lastBarMetric = await _dataService.GetLastMetric(order.Symbol);
                var currentPrice = position.AssetLastPrice.HasValue ? position.AssetLastPrice.Value : (await _brokerService.GetLastTrade(order.Symbol)).Price;
                var sellAmt = equity * MAX_SECURITY_SELL * order.Multiplier;
                var qty = Math.Floor(sellAmt / order.TargetPrice);
                var currentQty = position.Quantity;
                if (qty > currentQty)
                {
                    qty = currentQty;
                }
                _logger.LogInformation($"Selling {qty} shares of {order.Symbol} with multiplier {order.Multiplier}");
                BrokerOrder brokerOrder = await _brokerService.Sell(order.Symbol, qty, order.TargetPrice);
                if (brokerOrder != null)
                {
                    order.AppliedAmt += (qty * order.TargetPrice);
                    order.AvgEntryPrice = position.AverageEntryPrice;
                    order.BrokerOrderId = brokerOrder.BrokerOrderId;
                    orders = orders.Where(o => o.Id != order.Id).ToArray();
                    //just approximate here. it's probably fine
                    //_ = UpdateRemaining(qty * currentPrice * 0.1);
                    using (var stocksContext = _contextFactory.CreateDbContext())
                    {
                        stocksContext.Orders.Update(order);
                        await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    }
                    _logger.LogInformation($"Submitted sell order for {order.Symbol} at price {order.TargetPrice:C2}");
                }
                else if(qty > 0)
                {
                    _logger.LogError($"Failed to execute sell order for {order.Symbol}");
                }
            }
        }

        private async Task ExecuteBuys(DbOrder[] orders, double remainingBuyAmount)
        {
            var equity = _account.Balance.LastEquity;
            var positions = await _brokerService.GetPositions();
            foreach (var order in orders)
            {
                var position = positions.FirstOrDefault(p => p.Symbol == order.Symbol);
                var buy = equity * MAX_SECURITY_BUY * order.Multiplier;
                var currentPrice = position?.AssetLastPrice != null ? position.AssetLastPrice.Value : (await _brokerService.GetLastTrade(order.Symbol)).Price;
                if (remainingBuyAmount < buy) { buy = remainingBuyAmount; }
                var qty = Math.Floor(buy / order.TargetPrice);
                buy = qty * order.TargetPrice;
                _logger.LogInformation($"Buying {qty} shares of {order.Symbol} with multiplier {order.Multiplier}");
                var brokerOrder = await _brokerService.Buy(order.Symbol, qty, order.TargetPrice);
                if (brokerOrder != null)
                {
                    //just approximate here. it's probably fine
                    remainingBuyAmount -= buy;
                    _logger.LogInformation($"Submitted buy order for {order.Symbol} at price {order.TargetPrice:C2}. Remaining amount for buys {remainingBuyAmount:C2}");
                    order.AppliedAmt += buy;
                    order.BrokerOrderId = brokerOrder.BrokerOrderId;
                    using (var stocksContext = _contextFactory.CreateDbContext())
                    {
                        stocksContext.Orders.Update(order);
                        await stocksContext.SaveChangesAsync(_appCancellation.Token);
                    }
                }
                else if (qty > 0)
                {
                    _logger.LogError($"Failed to execute buy order for {order.Symbol}");
                }
            }
        }
        #endregion
    }
}
