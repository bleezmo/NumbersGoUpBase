using Newtonsoft.Json;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;

namespace NumbersGoUp.JsonModels
{
    public class FMPQuote
    {
        [JsonProperty("price")]
        public double Price { get; set; }
        [JsonProperty("eps")]
        public double EPS { get; set; }
        [JsonProperty("pe")]
        public double PERatio { get; set; }
        [JsonProperty("sharesOutstanding")]
        public double SharesOutstanding { get; set; }
        [JsonProperty("marketCap")]
        public double MarketCap { get; set; }
    }
    public class FMPIncomeQuarter
    {
        [JsonProperty("epsdiluted")]
        public double EPS { get; set; }
        [JsonProperty("ebitda")]
        public double EBITDA { get; set; }
    }
    public class FMPBalanceQuarter
    {
        [JsonProperty("totalCurrentAssets")]
        public double TotalCurrentAssets { get; set; }
        [JsonProperty("totalAssets")]
        public double TotalAssets { get; set; }
        [JsonProperty("totalCurrentLiabilities")]
        public double TotalCurrentLiabilities { get; set; }
        [JsonProperty("totalLiabilities")]
        public double TotalLiabilities { get; set; }
        [JsonProperty("totalEquity")]
        public double TotalEquity { get; set; }
        [JsonProperty("totalDebt")]
        public double TotalDebt { get; set; }
        [JsonProperty("cashAndCashEquivalents")]
        public double CashAndCashEquivalents { get; set; }
    }
    public class FMPCashFlowQuarter
    {
        [JsonProperty("dividendsPaid")]
        public double DividendsPaid { get; set; }
    }
    public class FMPHistorical
    {
        [JsonProperty("historical")]
        public IEnumerable<FMPPrice> Prices { get; set; }
    }
    public class FMPPrice
    {
        private DateTime _date = DateTime.MinValue;
        private double _price;
        [JsonProperty("date")]
        public string DateStr { get; set; }
        [JsonProperty("open")]
        public double Open { get; set; }
        [JsonProperty("high")]
        public double High { get; set; }
        [JsonProperty("low")]
        public double Low { get; set; }
        [JsonProperty("close")]
        public double Close { get; set; }
        [JsonIgnore]
        public DateTime? Date => _date != DateTime.MinValue ? _date : (!string.IsNullOrEmpty(DateStr) ? (DateTime.TryParse(DateStr, out _date) ? _date : null) : null);
        [JsonIgnore]
        public double Price => _price > 0 ? _price : _price = (Open + High + Low + Close) / 4;
    }
    public class KeyMetric
    {
        [JsonProperty("dividendYieldTTM")]
        public double DividentYield { get; set; }
        [JsonProperty("enterpriseValueOverEBITDATTM")]
        public double EVEBITDA { get; set; }
        [JsonProperty("peRatioTTM")]
        public double PERatio { get; set; }
    }
}
