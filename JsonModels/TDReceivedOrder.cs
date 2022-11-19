using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;

namespace NumbersGoUp.JsonModels
{
    public class TDReceivedOrder : TDOrder
    {
        public string OrderId { get; set; }
        public DateTime EnteredTime { get; set; }

        public DateTime? CloseTime { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public Status Status { get; set; }
        public IList<TDReceivedOrder> ChildOrderStrategies { get; set; }
    }
}
