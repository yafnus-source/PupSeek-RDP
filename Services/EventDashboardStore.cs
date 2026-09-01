using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    public static class EventDashboardStore
    {
        private static readonly object _lock = new object();
        private static readonly JsonSerializerOptions _opts = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // ---- Monitored computers (derived from AddressBookEntry.Monitored) ----
        public static List<string> GetMonitoredComputers()
        {
            try
            {
                // Primary source: AddressBookEntry.Monitored flag
                var fromBook = AddressBookStore.Load()
                    .Where(e => e.Monitored && !string.IsNullOrWhiteSpace(e.Computer))
                    .Select(e => e.Computer.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (fromBook.Count > 0) return fromBook;
                // Fallback: standalone file for manual entries
                return LoadMonitoredFile();
            }
            catch { return LoadMonitoredFile(); }
        }

        private static List<string> LoadMonitoredFile()
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.MonitoredComputersPath;
                    if (!File.Exists(path)) return new List<string>();
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<string>();
                    return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                }
                catch { return new List<string>(); }
            }
        }

        public static void SaveMonitoredComputers(List<string> list)
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.MonitoredComputersPath;
                    Directory.CreateDirectory(AppPaths.DataDir);
                    File.WriteAllText(path, JsonSerializer.Serialize(list ?? new List<string>(), _opts), new UTF8Encoding(false));
                }
                catch (Exception ex) { try { Logger.Warn($"EventDashboardStore SaveMonitored failed: {ex.Message}"); } catch { } }
            }
        }

        // ---- Events ----
        public static List<DashboardEvent> LoadEvents()
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.DashboardEventsPath;
                    if (!File.Exists(path)) return new List<DashboardEvent>();
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<DashboardEvent>();
                    var list = JsonSerializer.Deserialize<List<DashboardEvent>>(json) ?? new List<DashboardEvent>();
                    // cleanup >2 days
                    var cutoff = DateTime.Now.AddDays(-2);
                    var filtered = list.Where(e => e.Time >= cutoff).OrderByDescending(e => e.Time).ToList();
                    return filtered;
                }
                catch (Exception ex) { try { Logger.Warn($"LoadEvents failed: {ex.Message}"); } catch { } return new List<DashboardEvent>(); }
            }
        }

        public static void SaveEvents(List<DashboardEvent> events)
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.DashboardEventsPath;
                    Directory.CreateDirectory(AppPaths.DataDir);
                    // enforce retention
                    var cutoff = DateTime.Now.AddDays(-2);
                    var toSave = (events ?? new List<DashboardEvent>()).Where(e => e.Time >= cutoff).OrderByDescending(e => e.Time).Take(2000).ToList();
                    File.WriteAllText(path, JsonSerializer.Serialize(toSave, _opts), new UTF8Encoding(false));
                }
                catch (Exception ex) { try { Logger.Warn($"SaveEvents failed: {ex.Message}"); } catch { } }
            }
        }

        public static void AddEvents(IEnumerable<DashboardEvent> newEvents)
        {
            lock (_lock)
            {
                var existing = LoadEvents();
                var set = new HashSet<string>(existing.Select(e => $"{e.Computer}|{e.EventId}|{e.Time:O}|{e.Source}"));
                int added = 0;
                foreach (var ev in newEvents ?? Enumerable.Empty<DashboardEvent>())
                {
                    var key = $"{ev.Computer}|{ev.EventId}|{ev.Time:O}|{ev.Source}";
                    if (!set.Contains(key))
                    {
                        existing.Add(ev);
                        set.Add(key);
                        added++;
                    }
                }
                if (added > 0) SaveEvents(existing);
            }
        }

        // ---- Settings ----
        public static DashboardSettings LoadSettings()
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.DashboardSettingsPath;
                    if (!File.Exists(path)) return new DashboardSettings();
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new DashboardSettings();
                    return JsonSerializer.Deserialize<DashboardSettings>(json) ?? new DashboardSettings();
                }
                catch { return new DashboardSettings(); }
            }
        }

        public static void SaveSettings(DashboardSettings s)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    File.WriteAllText(AppPaths.DashboardSettingsPath, JsonSerializer.Serialize(s ?? new DashboardSettings(), _opts), new UTF8Encoding(false));
                }
                catch (Exception ex) { try { Logger.Warn($"SaveSettings failed: {ex.Message}"); } catch { } }
            }
        }
    }
}
