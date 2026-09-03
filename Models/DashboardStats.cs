using System;
using System.Collections.Generic;
using System.Linq;

namespace ShadowRDP.Models
{
    public sealed class DashboardStats
    {
        public int Total { get; set; }
        public int Critical { get; set; }
        public int Error { get; set; }
        public List<(string Key, int Count)> ByHost { get; set; } = new();
        public List<(string Key, int Count)> BySource { get; set; } = new();
        public List<(string Key, int Count)> ByEventId { get; set; } = new();
        public List<(DateTime Bucket, int Count)> TimeSeries { get; set; } = new();
        public string Period { get; set; } = "2 дня";

        public static DashboardStats From(List<DashboardEvent> events, string period)
        {
            var s = new DashboardStats { Period = period };
            if (events == null || events.Count == 0) return s;
            s.Total = events.Count;
            s.Critical = events.Count(e => string.Equals(e.Level, "Critical", StringComparison.OrdinalIgnoreCase));
            s.Error = events.Count(e => string.Equals(e.Level, "Error", StringComparison.OrdinalIgnoreCase));

            s.ByHost = events.GroupBy(e => string.IsNullOrWhiteSpace(e.Computer) ? "(неизвестно)" : e.Computer, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Key: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count).Take(10).ToList();

            s.BySource = events.GroupBy(e => string.IsNullOrWhiteSpace(e.Source) ? "(no source)" : e.Source, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Key: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count).Take(5).ToList();

            s.ByEventId = events.GroupBy(e => e.EventId.ToString())
                .Select(g => (Key: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count).Take(5).ToList();

            // Time series — bucket per period
            var now = DateTime.Now;
            TimeSpan span = period switch
            {
                "30 мин" => TimeSpan.FromMinutes(30),
                "1 час" => TimeSpan.FromHours(1),
                "24 часа" => TimeSpan.FromHours(24),
                _ => TimeSpan.FromDays(2)
            };
            var cutoff = now - span;
            var filtered = events.Where(e => e.Time >= cutoff).ToList();
            int buckets = period == "30 мин" ? 6 : period == "1 час" ? 6 : period == "24 часа" ? 24 : 16;
            double bucketHours = span.TotalHours / buckets;
            if (bucketHours < 0.08) bucketHours = 0.08; // ~5 min
            var series = new List<(DateTime, int)>();
            for (int i = 0; i < buckets; i++)
            {
                var bStart = cutoff.AddHours(bucketHours * i);
                var bEnd = cutoff.AddHours(bucketHours * (i + 1));
                int cnt = filtered.Count(e => e.Time >= bStart && e.Time < bEnd);
                series.Add((bStart, cnt));
            }
            s.TimeSeries = series;
            return s;
        }
    }
}
