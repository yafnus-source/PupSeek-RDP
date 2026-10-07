using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // Хранилище web-мониторинга (даш Z, ряд WEB). Схема как у ZabbixAgentStore:
    // lock + UTF8 без BOM + срез истории (2 дня / 5000 точек) + SyncToMonitor.
    public static class WebMonitorStore
    {
        private static readonly object _lock = new object();
        private static readonly JsonSerializerOptions _opts = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // ---- Проверки (URL) ----
        public static List<WebCheck> LoadUrls()
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.WebMonitorUrlsPath;
                    if (!File.Exists(path)) return new List<WebCheck>();
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<WebCheck>();
                    return (JsonSerializer.Deserialize<List<WebCheck>>(json) ?? new List<WebCheck>())
                        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Url)).ToList();
                }
                catch (Exception ex) { try { Logger.Warn($"WebMonitor LoadUrls: {ex.Message}"); } catch { } return new List<WebCheck>(); }
            }
        }

        public static void SaveUrls(IEnumerable<WebCheck> list)
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.WebMonitorUrlsPath;
                    Directory.CreateDirectory(AppPaths.DataDir);
                    var cleaned = (list ?? Enumerable.Empty<WebCheck>())
                        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Url))
                        .Select(c => { c.Url = c.Url.Trim(); if (string.IsNullOrWhiteSpace(c.Name)) c.Name = c.Url; return c; })
                        .ToList();
                    File.WriteAllText(path, JsonSerializer.Serialize(cleaned, _opts), new UTF8Encoding(false));
                    AppPaths.SyncToMonitor(path, "webmon_urls.json");
                }
                catch (Exception ex) { try { Logger.Warn($"WebMonitor SaveUrls: {ex.Message}"); } catch { } }
            }
        }

        // ---- История точек ----
        // v3.9.3 Фаза 3 — кэш (см. EventDashboardStore.LoadEvents).
        private static List<WebPoint>? _histCache;
        private static DateTime _histCacheMtime = DateTime.MinValue;
        private static long _histCacheLen = -1;

        public static List<WebPoint> LoadHistory()
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.WebMonitorHistoryPath;
                    if (!File.Exists(path)) { _histCache = new List<WebPoint>(); return new List<WebPoint>(); }
                    var fi = new FileInfo(path);
                    if (_histCache != null && fi.LastWriteTimeUtc == _histCacheMtime && fi.Length == _histCacheLen)
                        return new List<WebPoint>(_histCache);
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new List<WebPoint>();
                    var list = JsonSerializer.Deserialize<List<WebPoint>>(json) ?? new List<WebPoint>();
                    var cutoff = DateTime.Now.AddDays(-2);
                    var filtered = list.Where(x => x.Time >= cutoff).OrderByDescending(x => x.Time).Take(5000).ToList();
                    _histCache = filtered;
                    _histCacheMtime = fi.LastWriteTimeUtc;
                    _histCacheLen = fi.Length;
                    return new List<WebPoint>(filtered);
                }
                catch (Exception ex) { try { Logger.Warn($"WebMonitor LoadHistory: {ex.Message}"); } catch { } return new List<WebPoint>(); }
            }
        }

        public static void SaveHistory(List<WebPoint> list)
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.WebMonitorHistoryPath;
                    Directory.CreateDirectory(AppPaths.DataDir);
                    var cutoff = DateTime.Now.AddDays(-2);
                    var toSave = (list ?? new List<WebPoint>()).Where(x => x.Time >= cutoff).OrderByDescending(x => x.Time).Take(5000).ToList();
                    File.WriteAllText(path, JsonSerializer.Serialize(toSave, _opts), new UTF8Encoding(false));
                    _histCache = null; // v3.9.3 Фаза 3 — инвалидация кэша
                    AppPaths.SyncToMonitor(path, "webmon_history.json");
                }
                catch (Exception ex) { try { Logger.Warn($"WebMonitor SaveHistory: {ex.Message}"); } catch { } }
            }
        }

        public static void AddPoints(IEnumerable<WebPoint> points)
        {
            lock (_lock)
            {
                var existing = LoadHistory();
                existing.AddRange(points ?? Enumerable.Empty<WebPoint>());
                SaveHistory(existing);
            }
        }

        public static WebPoint? LatestFor(string url)
        {
            return LoadHistory()
                .Where(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Time)
                .FirstOrDefault();
        }

        public static void DeleteUrlHistory(string url)
        {
            lock (_lock)
            {
                var existing = LoadHistory();
                var filtered = existing.Where(x => !string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase)).ToList();
                if (filtered.Count != existing.Count) SaveHistory(filtered);
            }
        }

        // ---- Настройки ----
        public static WebMonitorSettings LoadSettings()
        {
            lock (_lock)
            {
                try
                {
                    var path = AppPaths.WebMonitorSettingsPath;
                    if (!File.Exists(path)) return new WebMonitorSettings();
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json)) return new WebMonitorSettings();
                    return JsonSerializer.Deserialize<WebMonitorSettings>(json) ?? new WebMonitorSettings();
                }
                catch { return new WebMonitorSettings(); }
            }
        }

        public static void SaveSettings(WebMonitorSettings s)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    File.WriteAllText(AppPaths.WebMonitorSettingsPath, JsonSerializer.Serialize(s ?? new WebMonitorSettings(), _opts), new UTF8Encoding(false));
                }
                catch (Exception ex) { try { Logger.Warn($"WebMonitor SaveSettings: {ex.Message}"); } catch { } }
            }
        }
    }
}
