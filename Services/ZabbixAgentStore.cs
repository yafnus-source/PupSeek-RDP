using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    public static class ZabbixAgentStore
    {
        private static readonly object _lock = new object();
        // v3.9.3 Фаза 3 — кэш истории (файл ~1МБ, читался на каждый опрос
        // и детали хоста). См. EventDashboardStore.LoadEvents.
        private static List<ZabbixAgentData>? _histCache;
        private static DateTime _histCacheMtime = DateTime.MinValue;
        private static long _histCacheLen = -1;
        private static readonly JsonSerializerOptions _opts = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public static List<ZabbixAgentData> LoadHistory()
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.ZabbixAgentHistoryPath;
                    if (!File.Exists(path)) { _histCache = new List<ZabbixAgentData>(); return new List<ZabbixAgentData>(); }
                    var fi = new FileInfo(path);
                    if (_histCache != null && fi.LastWriteTimeUtc == _histCacheMtime && fi.Length == _histCacheLen)
                        return new List<ZabbixAgentData>(_histCache);
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<ZabbixAgentData>();
                    var list = JsonSerializer.Deserialize<List<ZabbixAgentData>>(json) ?? new List<ZabbixAgentData>();
                    var cutoff = DateTime.Now.AddDays(-2);
                    var filtered = list.Where(x => x.Time >= cutoff).OrderByDescending(x => x.Time).Take(5000).ToList();
                    _histCache = filtered;
                    _histCacheMtime = fi.LastWriteTimeUtc;
                    _histCacheLen = fi.Length;
                    return new List<ZabbixAgentData>(filtered);
                }
                catch (Exception ex) { try { Logger.Warn($"ZabbixAgent LoadHistory: {ex.Message}"); } catch { } return new List<ZabbixAgentData>(); }
            }
        }

        public static void SaveHistory(List<ZabbixAgentData> list)
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.ZabbixAgentHistoryPath;
                    Directory.CreateDirectory(AppPaths.DataDir);
                    var cutoff = DateTime.Now.AddDays(-2);
                    var toSave = (list ?? new List<ZabbixAgentData>()).Where(x => x.Time >= cutoff).OrderByDescending(x => x.Time).Take(5000).ToList();
                    File.WriteAllText(path, JsonSerializer.Serialize(toSave, _opts), new UTF8Encoding(false));
                    _histCache = null; // v3.9.3 Фаза 3 — инвалидация кэша
                    AppPaths.SyncToMonitor(path, "zabbix_agent_history.json");
                }
                catch (Exception ex) { try { Logger.Warn($"ZabbixAgent SaveHistory: {ex.Message}"); } catch { } }
            }
        }

        public static void AddPoints(IEnumerable<ZabbixAgentData> points)
        {
            lock (_lock)
            {
                var existing = LoadHistory();
                existing.AddRange(points ?? Enumerable.Empty<ZabbixAgentData>());
                SaveHistory(existing);
            }
        }

        public static ZabbixAgentSettings LoadSettings()
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.ZabbixAgentSettingsPath;
                    if (!File.Exists(path)) return new ZabbixAgentSettings();
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new ZabbixAgentSettings();
                    return JsonSerializer.Deserialize<ZabbixAgentSettings>(json) ?? new ZabbixAgentSettings();
                }
                catch { return new ZabbixAgentSettings(); }
            }
        }

        private static ZabbixAgentSettings _cachedSettings;
        private static DateTime _cacheTime;
        public static ZabbixAgentSettings LoadSettingsCached()
        {
            if (_cachedSettings != null && (DateTime.Now - _cacheTime).TotalSeconds < 3) return _cachedSettings;
            _cachedSettings = LoadSettings();
            _cacheTime = DateTime.Now;
            return _cachedSettings;
        }

        public static void SaveSettings(ZabbixAgentSettings s)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    File.WriteAllText(AppPaths.ZabbixAgentSettingsPath, JsonSerializer.Serialize(s ?? new ZabbixAgentSettings(), _opts), new UTF8Encoding(false));
                    _cachedSettings = null;
                }
                catch (Exception ex) { try { Logger.Warn($"ZabbixAgent SaveSettings: {ex.Message}"); } catch { } }
            }
        }

        public static List<ZabbixAgentData> GetLatestPerHost()
        {
            var all = LoadHistory();
            return all.GroupBy(x => x.Host, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.Time).First())
                .OrderBy(x => x.Host).ToList();
        }

        public static void DeleteHostHistory(string host)
        {
            lock (_lock)
            {
                var existing = LoadHistory();
                var filtered = existing.Where(x => !string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase)).ToList();
                if (filtered.Count != existing.Count) SaveHistory(filtered);
            }
        }

        public static List<AddressBookEntry> LoadManualHosts()
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.ZabbixManualHostsPath;
                    if (!File.Exists(path)) return new List<AddressBookEntry>();
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<AddressBookEntry>();
                    return JsonSerializer.Deserialize<List<AddressBookEntry>>(json) ?? new List<AddressBookEntry>();
                }
                catch (Exception ex) { try { Logger.Warn($"ZabbixAgent LoadManualHosts: {ex.Message}"); } catch { } return new List<AddressBookEntry>(); }
            }
        }

        public static void SaveManualHosts(IEnumerable<AddressBookEntry> list)
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.ZabbixManualHostsPath;
                    Directory.CreateDirectory(AppPaths.DataDir);
                    var cleaned = (list ?? Enumerable.Empty<AddressBookEntry>())
                        .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Computer)).ToList();
                    File.WriteAllText(path, JsonSerializer.Serialize(cleaned, _opts), new UTF8Encoding(false));
                    AppPaths.SyncToMonitor(path, "zabbix_manual_hosts.json");
                }
                catch (Exception ex) { try { Logger.Warn($"ZabbixAgent SaveManualHosts: {ex.Message}"); } catch { } }
            }
        }

        public static void DeleteManualHost(string host)
        {
            lock (_lock)
            {
                var existing = LoadManualHosts();
                var filtered = existing.Where(e => !string.Equals(e.Computer, host, StringComparison.OrdinalIgnoreCase)).ToList();
                if (filtered.Count != existing.Count) SaveManualHosts(filtered);
            }
        }
    }
}
