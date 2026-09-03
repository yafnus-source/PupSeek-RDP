using System;
using System.Text.Json.Serialization;

namespace ShadowRDP.Models
{
    public class DashboardEvent
    {
        [JsonPropertyName("time")]
        public DateTime Time { get; set; }

        [JsonPropertyName("level")]
        public string Level { get; set; } = ""; // Error / Critical

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("eventId")]
        public int EventId { get; set; }

        [JsonPropertyName("computer")]
        public string Computer { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("logName")]
        public string LogName { get; set; } = "";

        [JsonPropertyName("notified")]
        public bool Notified { get; set; } = false;

        [JsonPropertyName("decoded")]
        public string Decoded { get; set; } = "";
    }

    public class DashboardSettings
    {
        [JsonPropertyName("pollIntervalMinutes")]
        public int PollIntervalMinutes { get; set; } = 30;

        [JsonPropertyName("retentionDays")]
        public int RetentionDays { get; set; } = 2;

        [JsonPropertyName("notifyCritical")]
        public bool NotifyCritical { get; set; } = true;

        [JsonPropertyName("notifyError")]
        public bool NotifyError { get; set; } = false;

        [JsonPropertyName("notifySound")]
        public bool NotifySound { get; set; } = false;

        [JsonPropertyName("showNyanCat")]
        public bool ShowNyanCat { get; set; } = true;

        [JsonPropertyName("aiProvider")]
        public string AiProvider { get; set; } = "ollama"; // ollama | fallback
    }
}
