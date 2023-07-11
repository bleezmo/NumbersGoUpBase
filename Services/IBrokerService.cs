using Microsoft.Extensions.Hosting;
using NumbersGoUp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.Services
{
    public interface IBrokerService : IDisposable
    {
        Task Ready();

        Task<IEnumerable<HistoryBar>> GetBarHistoryDay(string symbol, DateTime from);

        Task<TickerInfo> GetTickerInfo(string symbol);

        Task<Quote> GetLastTrade(string symbol);

        Task<IEnumerable<Position>> GetPositions();

        Task<Position> GetPosition(string symbol);

        Task<BrokerOrder> Sell(string symbol, double qty, double? limit = null);

        Task<BrokerOrder> Buy(string symbol, double qty, double? limit = null);

        Task<BrokerOrder> ClosePositionAtMarket(string symbol);

        Task<IEnumerable<BrokerOrder>> GetOpenOrders();

        Task<IEnumerable<BrokerOrder>> GetClosedOrders(DateTime? from = null);

        Task<BrokerOrder> GetOrder(string brokerOrderId);

        Task<DateTime> GetMarketOpen();

        Task<MarketDay> GetLastMarketDay();

        Task<DateTime> GetMarketClose();

        Task<Account> GetAccount();

        Task<Financials> GetFinancials(string symbol);

        Task<IEnumerable<MarketDay>> GetMarketDays(int year, int? month = null);
        Task<(Dictionary<string, List<AccountHistoryEvent>> trades, double dividends)> GetAccountHistory();
    }
}
