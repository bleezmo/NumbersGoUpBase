using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NumbersGoUp.Models;
using NumbersGoUp.Utils;
using NumbersGoUpBase.Services;

namespace NumbersGoUp.Services
{
    public class TraderService
    {
        public const double MAX_COOLDOWN_DAYS = 10;
        private const string DISABLE_SELLS = "DisableSells";
        private const string DISABLE_BUYS = "DisableBuys";
        public const string MAX_DAILY_BUY = "MaxDailyBuy";

        private readonly IAppCancellation _appCancellation;
        private readonly ILogger<TraderService> _logger;
        private readonly IBrokerService _brokerService;
        private readonly RebalancerService _rebalancerService;
        private readonly TickerService _tickerService;
        private readonly DataService _dataService;
        private readonly string _environmentName;
        private readonly IStocksContextFactory _contextFactory;
        private readonly bool _disableBuys;
        private readonly bool _disableSells;
        private readonly double _maxDailyBuy;
        private Account _account;
        private double _cashEquityRatio;

        public TraderService(IConfiguration configuration, IAppCancellation appCancellation, IHostEnvironment environment, ILogger<TraderService> logger, TickerService tickerService, 
                             IBrokerService brokerService, RebalancerService rebalancerService, DataService dataService, IStocksContextFactory contextFactory)
        {
            _appCancellation = appCancellation;
            _logger = logger;
            _brokerService = brokerService;
            _rebalancerService = rebalancerService;
            _tickerService = tickerService;
            _dataService = dataService;
            _environmentName = environment.EnvironmentName;
            _contextFactory = contextFactory;
            _disableBuys = bool.TryParse(configuration[DISABLE_BUYS], out var disableBuys) ? disableBuys : false;
            _disableSells = bool.TryParse(configuration[DISABLE_SELLS], out var disableSells) ? disableSells : false;
            _maxDailyBuy = double.TryParse(configuration[MAX_DAILY_BUY], out var maxBuy) ? maxBuy : 2000;
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
                    if (_account.Balance.LastEquity == 0)
                    {
                        _logger.LogError("Error retrieving equity value! Shutting down");
                        return;
                    }
                    var equity = _account.Balance.TradeableEquity;
                    var cash = _account.Balance.TradableCash;
                    var positions = await _brokerService.GetPositions();
                    var currentBarMetrics = new List<BarMetric>();
                    using (var stocksContext = _contextFactory.CreateDbContext())
                    {
                        foreach(var position in positions.Where(p => !_rebalancerService.BondSymbols.Contains(p.Symbol)))
                        {
                            var barMetric = await stocksContext.BarMetrics.Where(b => b.Symbol == position.Symbol).OrderByDescending(m => m.BarDayMilliseconds).Take(1).FirstOrDefaultAsync(_appCancellation.Token);
                            if (barMetric != null) { currentBarMetrics.Add(barMetric); }
                            else { _logger.LogError($"Bar metric not found for position {position.Symbol}"); }
                        }
                    }
                    var cashEquityRatioOffset = 1.0;
                    if (currentBarMetrics.Count > 1)
                    {
                        var smasmaMode = currentBarMetrics.OrderBy(b => b.SMASMA).Skip(currentBarMetrics.Count / 2).Take(1).First().SMASMA;
                        cashEquityRatioOffset = smasmaMode.DoubleReduce(0, -20);
                    }
                    _cashEquityRatio = equity > 0 ? (Math.Max(cash / equity, 0) * cashEquityRatioOffset) : 0;
                    _logger.LogInformation($"Using Cash-Equity Ratio: {_cashEquityRatio}");

                    _logger.LogInformation("Running previous-day metrics");
                    await PreviousDayTradeMetrics();
                    if (cash < 0)
                    {
                        _logger.LogError("Negative cash balance!");
                    }
                    _logger.LogInformation("Running rebalancer");
                    var rebalancers = await _rebalancerService.Rebalance(positions, new Balance
                    {
                        TradeableEquity = equity,
                        TradableCash = cash
                    });
                    _logger.LogInformation("Running order executions");
                    await ExecuteOrders(rebalancers);
                    _logger.LogInformation("Cleaning up");
                    await CleanUp();
                    await AccountPerformancePrint(positions);
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

        private async Task PreviousDayTradeMetrics()
        {
            var orders = await GetPreviousTradingDayFilledOrders();
            using var stocksContext = _contextFactory.CreateDbContext();
            foreach (var (order, brokerOrder) in orders)
            {
                var profitLossPerc = 0.0;
                var dayOfWeek = brokerOrder.FilledAt.Value.DayOfWeek;
                var daysToNextBuy = brokerOrder.OrderSide == OrderSide.Buy ? (1 - _cashEquityRatio.DoubleReduce(2, 0.2)) * MAX_COOLDOWN_DAYS : MAX_COOLDOWN_DAYS; 
                var daysToNextSell = brokerOrder.OrderSide == OrderSide.Sell ? _cashEquityRatio.DoubleReduce(0.2, -0.2) * MAX_COOLDOWN_DAYS : MAX_COOLDOWN_DAYS;
                if (order != null && brokerOrder.OrderSide == OrderSide.Sell && order.AvgEntryPrice > 0)
                {
                    profitLossPerc = (brokerOrder.AverageFillPrice.Value - order.AvgEntryPrice) * 100 / order.AvgEntryPrice;
                    if (profitLossPerc < 0)
                    {
                        daysToNextBuy = 64;
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
        private async Task ExecuteOrders(IEnumerable<IRebalancer> rebalancers)
        {
            var now = DateTime.Now;
            var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            List<DbOrder> currentOrders;
            using (var stocksContext = _contextFactory.CreateDbContext())
            {
                currentOrders = await stocksContext.Orders.Where(o => o.Account == _account.AccountId && o.TimeLocalMilliseconds > dayStart).Include(o => o.Ticker).ToListAsync(_appCancellation.Token);
            }
            rebalancers = rebalancers.Where(r => !currentOrders.Any(o => o.Symbol == r.Symbol));
            var (stocks, bonds) = (rebalancers.Where(r => r.IsStock).Select(r => r as StockRebalancer), rebalancers.Where(r => r.IsBond).Select(r => r as BondRebalancer));

            var remainingBuyAmount = Math.Min(_maxDailyBuy, _account.Balance.TradableCash);

            remainingBuyAmount -= currentOrders.Select(o => o.Side == OrderSide.Buy ? o.AppliedAmt : 0).Sum();
            _logger.LogInformation($"Starting balance {_account.Balance.TradableCash:C2} and remaining buy amount {remainingBuyAmount:C2}");
            foreach(var bond in bonds)
            {
                if (bond.Diff > 0)
                {
                    remainingBuyAmount = await ExecuteBondBuy(bond, remainingBuyAmount);
                }
                else if (bond.Diff < 0) { await ExecuteBondSell(bond); }
            }
            _logger.LogInformation("Executing sells");
            await ExecuteSells(stocks.Where(r => r.Diff < 0).ToArray());
            _logger.LogInformation("Executing buys");
            await ExecuteBuys(stocks.Where(r => r.Diff > 0).ToArray(), remainingBuyAmount);
        }
        private async Task ExecuteSells(StockRebalancer[] rebalancers)
        {
            if (_disableSells) 
            {
                _logger.LogInformation("Sells disabled.");
                return; 
            }
            var now = DateTime.UtcNow;
            var equity = _account.Balance.TradeableEquity;
            foreach (var rebalancer in rebalancers)
            {
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var lastSellOrder = await stocksContext.OrderHistories.Where(o => o.Account == _account.AccountId && o.Symbol == rebalancer.Symbol && o.NextSell != null).OrderByDescending(o => o.TimeLocalMilliseconds).Take(1).FirstOrDefaultAsync(_appCancellation.Token);
                    if (lastSellOrder != null && lastSellOrder.NextSell.Value.Date.CompareTo(now) > 0) continue;
                }
                var position = rebalancer.Position;
                if (position == null)
                {
                    _logger.LogError($"No position found for {rebalancer.Symbol}!!! Can't Sell!!!");
                    continue;
                }
                var lastBarMetric = await _dataService.GetLastMetric(rebalancer.Symbol);
                var targetPrice = lastBarMetric.HistoryBar.ClosePrice;
                if(targetPrice == 0)
                {
                    _logger.LogError($"Target price zero when selling. Ticker {position.Symbol}");
                    continue;
                }
                var sellAmt = Math.Abs(rebalancer.Diff);
                var currentQty = position.Quantity;
                var qty = sellAmt == position.MarketValue ? currentQty : Math.Floor(sellAmt / targetPrice);
                if (qty > currentQty)
                {
                    qty = currentQty;
                }
                if(qty > 0)
                {
                    _logger.LogInformation($"Selling {qty} shares of {rebalancer.Symbol} with multiplier {rebalancer.Prediction.SellMultiplier}");
                    BrokerOrder brokerOrder = await _brokerService.Sell(rebalancer.Symbol, qty, targetPrice);
                    if (brokerOrder != null)
                    {
                        using (var stocksContext = _contextFactory.CreateDbContext())
                        {
                            var lastBuyOrder = await stocksContext.OrderHistories.Where(o => o.Account == _account.AccountId && o.Symbol == rebalancer.Symbol && o.Side == OrderSide.Buy).OrderByDescending(o => o.TimeLocalMilliseconds).Take(1).FirstOrDefaultAsync(_appCancellation.Token);

                            stocksContext.Orders.Add(new DbOrder
                            {
                                TimeLocal = now,
                                TimeLocalMilliseconds = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
                                DaysFromLastBuy = Convert.ToInt32(Math.Floor(now.Subtract(lastBuyOrder?.TimeLocal ?? now).TotalDays)),
                                TickerId = rebalancer.Ticker.Id,
                                Symbol = rebalancer.Symbol,
                                TargetPrice = targetPrice,
                                Side = OrderSide.Sell,
                                Multiplier = rebalancer.Prediction.SellMultiplier,
                                Account = _account.AccountId,
                                Qty = qty,
                                AppliedAmt = qty * targetPrice,
                                AvgEntryPrice = position.AverageEntryPrice,
                                BrokerOrderId = brokerOrder.BrokerOrderId
                            });
                            await stocksContext.SaveChangesAsync(_appCancellation.Token);
                        }
                        _logger.LogInformation($"Submitted sell order for {rebalancer.Symbol} at price {targetPrice:C2}");
                    }
                    else
                    {
                        _logger.LogError($"Failed to execute sell order for {rebalancer.Symbol}");
                    }
                }
            }
        }

        private double priorityOrdering(BuyState bss) => bss.Rebalancer.Ticker.PerformanceVector;// * bss.ProfitLossPerc.ZeroReduce(bss.Rebalancer.Ticker.ProfitLossAvg + bss.Rebalancer.Ticker.ProfitLossStDev, (bss.Rebalancer.Ticker.ProfitLossAvg + bss.Rebalancer.Ticker.ProfitLossStDev) * -1);
        private async Task ExecuteBuys(StockRebalancer[] rebalancers, double remainingBuyAmount)
        {
            if (_disableBuys) 
            {
                _logger.LogInformation("Buys disabled.");
                return; 
            }
            var now = DateTime.UtcNow;
            var equity = _account.Balance.TradeableEquity;
            var buys = new List<BuyState>();
            foreach(var rebalancer in rebalancers)
            {
                double percProfit = 0.0;
                if(rebalancer.Position != null) 
                {
                    if (rebalancer.Position.UnrealizedProfitLossPercent.HasValue)
                    {
                        percProfit = rebalancer.Position.UnrealizedProfitLossPercent.Value * 100;
                    }
                    else
                    {
                        var currentPrice = rebalancer.Position?.AssetLastPrice != null ? rebalancer.Position.AssetLastPrice.Value : (await _brokerService.GetLastTrade(rebalancer.Symbol)).Price;
                        if(rebalancer.Position.CostBasis > 0)
                        {
                            percProfit = (currentPrice - rebalancer.Position.CostBasis) * 100 / rebalancer.Position.CostBasis;
                        }
                        else
                        {
                            _logger.LogError($"Cost basis somehow zero. Ticker {rebalancer.Position.Symbol}");
                        }
                    }
                }
                buys.Add(new BuyState
                {
                    ProfitLossPerc = percProfit,
                    Rebalancer = rebalancer
                });
            }
            foreach (var buy in buys.OrderByDescending(priorityOrdering))
            {
                var rebalancer = buy.Rebalancer;
                using (var stocksContext = _contextFactory.CreateDbContext())
                {
                    var lastBuyOrder = await stocksContext.OrderHistories.Where(o => o.Account == _account.AccountId && o.Symbol == rebalancer.Symbol && o.NextBuy != null).OrderByDescending(o => o.TimeLocalMilliseconds).Take(1).FirstOrDefaultAsync(_appCancellation.Token);
                    if (lastBuyOrder != null && lastBuyOrder.NextBuy.Value.Date.CompareTo(now) > 0) continue;
                }
                var position = rebalancer.Position;
                var buyAmt = rebalancer.Diff;
                if (remainingBuyAmount < buyAmt) { buyAmt = remainingBuyAmount; }
                var lastBarMetric = await _dataService.GetLastMetric(rebalancer.Symbol);
                var targetPrice = Math.Min(lastBarMetric.HistoryBar.Price(), lastBarMetric.HistoryBar.ClosePrice);
                if (targetPrice == 0)
                {
                    _logger.LogError($"Target price zero when buying. Ticker {position.Symbol}");
                    continue;
                }
                var qty = Math.Floor(buyAmt / targetPrice);
                if(qty > 0)
                {
                    buyAmt = qty * targetPrice;
                    _logger.LogInformation($"Buying {qty} shares of {rebalancer.Symbol} with multiplier {rebalancer.Prediction.BuyMultiplier}");
                    var brokerOrder = await _brokerService.Buy(rebalancer.Symbol, qty, targetPrice);
                    if (brokerOrder != null)
                    {
                        //just approximate here. it's probably fine
                        remainingBuyAmount -= buyAmt;
                        using (var stocksContext = _contextFactory.CreateDbContext())
                        {
                            stocksContext.Orders.Add(new DbOrder
                            {
                                TimeLocal = now,
                                TimeLocalMilliseconds = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
                                DaysFromLastBuy = 0,
                                TickerId = rebalancer.Ticker.Id,
                                Symbol = rebalancer.Symbol,
                                TargetPrice = targetPrice,
                                Side = OrderSide.Buy,
                                Multiplier = rebalancer.Prediction.BuyMultiplier,
                                Account = _account.AccountId,
                                Qty = qty,
                                AppliedAmt = qty * targetPrice,
                                AvgEntryPrice = position?.AverageEntryPrice ?? 0,
                                BrokerOrderId = brokerOrder.BrokerOrderId
                            });
                            await stocksContext.SaveChangesAsync(_appCancellation.Token);
                        }
                        _logger.LogInformation($"Submitted buy order for {rebalancer.Symbol} at price {targetPrice:C2}. Remaining amount for buys {remainingBuyAmount:C2}");
                    }
                    else
                    {
                        _logger.LogError($"Failed to execute buy order for {rebalancer.Symbol}");
                    }
                }
            }
        }

        private async Task ExecuteBondSell(BondRebalancer rebalancer)
        {
            var position = rebalancer.Position;
            if (position == null)
            {
                _logger.LogError($"No position found for {rebalancer.Symbol}!!! Can't Sell!!!");
                return;
            }
            var targetPrice = position.AssetLastPrice.HasValue ? position.AssetLastPrice.Value : (await _brokerService.GetLastTrade(rebalancer.Symbol)).Price;
            var sellAmt = Math.Abs(rebalancer.Diff);
            if (targetPrice > 0)
            {
                var qty = Math.Floor(sellAmt / targetPrice);
                var currentQty = position.Quantity;
                if (qty > currentQty)
                {
                    qty = currentQty;
                }
                if (qty > 0)
                {
                    _logger.LogInformation($"Selling {qty} shares of bond {rebalancer.Symbol}");
                    BrokerOrder brokerOrder = await _brokerService.Sell(rebalancer.Symbol, qty, targetPrice);
                    if (brokerOrder != null)
                    {
                        _logger.LogInformation($"Submitted sell order for {rebalancer.Symbol} at price {targetPrice:C2}");
                    }
                    else
                    {
                        _logger.LogError($"Failed to execute sell order for {rebalancer.Symbol}");
                    }
                }
            }
            else
            {
                _logger.LogError($"Target price zero when selling bond. Ticker {position.Symbol}");
            }
        }

        private async Task<double> ExecuteBondBuy(BondRebalancer rebalancer, double remainingBuyAmount)
        {
            var position = rebalancer.Position;
            var buy = rebalancer.Diff;
            if (remainingBuyAmount < buy) { buy = remainingBuyAmount; }
            var targetPrice = position?.AssetLastPrice != null ? position.AssetLastPrice.Value : (await _brokerService.GetLastTrade(rebalancer.Symbol)).Price;
            if(targetPrice > 0)
            {
                var qty = Math.Floor(buy / targetPrice);
                if (qty > 0)
                {
                    buy = qty * targetPrice;
                    _logger.LogInformation($"Buying {qty} shares of bond {rebalancer.Symbol}");
                    var brokerOrder = await _brokerService.Buy(rebalancer.Symbol, qty, targetPrice);
                    if (brokerOrder != null)
                    {
                        //just approximate here. it's probably fine
                        remainingBuyAmount -= buy;
                        _logger.LogInformation($"Submitted buy order for bond {rebalancer.Symbol} at price {targetPrice:C2}. Remaining amount for buys {remainingBuyAmount:C2}");
                    }
                    else
                    {
                        _logger.LogError($"Failed to execute buy order for {rebalancer.Symbol}");
                    }
                }
            }
            else
            {
                _logger.LogError($"Target price zero when buying bond. Ticker {position.Symbol}");
            }
            return remainingBuyAmount;
        }
        private async Task AccountPerformancePrint(IEnumerable<Position> positions)
        {
            var (historyEvents, dividends) = await _brokerService.GetAccountHistory();
            var totalRealizedProfits = new List<(double cost, double profitPerc)>();
            var totalUnRealizedProfits = new List<(double cost, double profitPerc)>();
            foreach (var historyEvent in historyEvents)
            {
                var events = historyEvent.Value.OrderBy(e => e.Date).ToArray();
                var eventQueue = new Queue<double>();
                var profits = new List<(double cost, double profitPerc)>();
                for (var i = 0; i < events.Length; i++)
                {
                    if (events[i].Amount < 0)
                    {
                        if (i == 0 || events[i - 1].Date.CompareTo(events[i].Date) != 0 || events[i - 1].Amount < 0)
                        {
                            eventQueue.EnqueueEventDetails(events[i]);
                        }
                    }
                    else if (events[i].Amount > 0)
                    {
                        var qtySold = Math.Abs(events[i].Qty);
                        var sellPrice = events[i].Price;
                        for (var dc = 0; qtySold > 0 && dc < 10000; dc++)
                        {
                            qtySold--;
                            if (eventQueue.Any())
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
            foreach (var position in positions)
            {
                if (position.UnrealizedProfitLossPercent.HasValue && !_rebalancerService.BondSymbols.Any(s => s == position.Symbol))
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
            _logger.LogInformation($"Total Account Equity: {_account.Balance.LastEquity:C2} Total Cost Basis: {totalCost:C2} Total Profit: {totalProfit * 100:0.0000}% Dividends: {dividends:C2}");
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
    }
    public class BuyState
    {
        public StockRebalancer Rebalancer { get; set; }
        public double ProfitLossPerc { get; set; }
    }
}
