using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace WarzoneHelper.Core.Events
{
    /// <summary>
    /// A single event dispatched by any monitor. Serialized to JSON when it crosses
    /// the plugin boundary into Overwolf JS (Overwolf marshals plugin event args as
    /// strings/objects, so a flat JSON string is the safest contract).
    /// </summary>
    public sealed class HelperEvent
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("source")]
        public string Source { get; set; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }

        /// <summary>Arbitrary event-specific payload.</summary>
        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }

        public HelperEvent()
        {
            Data = new Dictionary<string, object>();
            Timestamp = DateTime.UtcNow.ToString("o");
        }

        public HelperEvent(string name, string source) : this()
        {
            Name = name;
            Source = source;
        }

        public HelperEvent With(string key, object value)
        {
            Data[key] = value;
            return this;
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this);
        }

        public override string ToString()
        {
            return ToJson();
        }
    }
}
