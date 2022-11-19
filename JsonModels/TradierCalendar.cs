using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NumbersGoUp.JsonModels
{
    public class TradierCalendarWrapper
    {
        [JsonProperty("calendar")]
        public TradierCalendar Calendar { get; set; }
    }
    public class TradierCalendar
    {
        [JsonProperty("month")]
        public int Month { get; set; }
        [JsonProperty("year")]
        public int Year { get; set; }
        [JsonProperty("days")]
        public DayWrapper DayWrapper { get; set; }
    }
    public class DayWrapper
    {
        [JsonProperty("day")]
        public MarketDay[] Days { get; set; }
    }
    public class MarketDay
    {
        [JsonProperty("date")]
        public string DateString { get; set; }
        [JsonProperty("status")]
        public string Status { get; set; }
        [JsonProperty("open")]
        public DayInterval OpenInterval { get; set; }
        [JsonIgnore]
        public DateTime TradingTimeOpenEST => DateTime.Parse($"{DateString} {OpenInterval.Start}", null, DateTimeStyles.AssumeLocal);
        [JsonIgnore]
        public DateTime TradingTimeCloseEST => DateTime.Parse($"{DateString} {OpenInterval.End}", null, DateTimeStyles.AssumeLocal);
    }
    public class DayInterval
    {
        [JsonProperty("start")]
        public string Start { get; set; }
        [JsonProperty("end")]
        public string End { get; set; }
    }
}
