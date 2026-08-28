using System;

namespace ShadowRDP.Models
{
    // v3.6.8 — an MMC console the user embedded into RDP Manager via the
    // "Встроить ММС" button. Persisted in embedded_apps.json so the history
    // of previously embedded consoles survives app restarts.
    // NO credentials stored — just the console file path and display name.
    public class EmbeddedAppEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Arguments { get; set; } = "";
        // ISO-8601 UTC timestamp of the last time the console was embedded.
        // History is shown most-recently-used-first.
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    }
}
