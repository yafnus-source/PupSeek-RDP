using System;
using System.Text.Json.Serialization;

namespace ShadowRDP.Models
{
    // Одна web-проверка даша Z (ряд WEB): что опрашиваем и как.
    public class WebCheck
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("timeoutMs")]
        public int TimeoutMs { get; set; } = 10000;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;
    }

    // Одна точка опроса: жив ли ресурс, код ответа, время ответа.
    public class WebPoint
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("time")]
        public DateTime Time { get; set; } = DateTime.Now;

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("statusCode")]
        public int StatusCode { get; set; }

        [JsonPropertyName("ms")]
        public long Ms { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; } = "";
    }

    public class WebMonitorSettings
    {
        [JsonPropertyName("intervalMinutes")]
        public int IntervalMinutes { get; set; } = 5;

        [JsonPropertyName("timeoutMs")]
        public int TimeoutMs { get; set; } = 10000;

        [JsonPropertyName("concurrency")]
        public int Concurrency { get; set; } = 8;
    }
}
