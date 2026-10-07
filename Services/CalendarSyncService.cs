using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ShadowRDP.Helpers;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // v4.9.4 — автосинк календаря по URL (webcal/https .ics): раз в
    // CalendarSyncMinutes скачиваем календарь, парсим IcsParser'ом и
    // добавляем новые события (дедуп по calendarUid) как source="calendar".
    // Пустой CalendarUrl = фича выключена (Start сразу выходит).
    public static class CalendarSyncService
    {
        private static readonly object _lock = new object();
        private static System.Threading.Timer? _timer;
        private static HttpClient? _http;
        private static bool _running;
        private static int _busy;

        // added — сколько новых событий добавилось за проход.
        public static event Action<int>? OnCalendarSynced;

        public static void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _running = true;
            }
            try
            {
                var settings = TodoStore.LoadSettings();
                if (string.IsNullOrWhiteSpace(settings.CalendarUrl))
                {
                    Logger.Info("CalendarSyncService: no CalendarUrl, idle");
                    return;
                }
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
                int minutes = Math.Clamp(settings.CalendarSyncMinutes, 5, 120);
                var span = TimeSpan.FromMinutes(minutes);
                // Первый проход — почти сразу, дальше по таймеру.
                _timer = new System.Threading.Timer(_ => _ = SyncNowAsync(), null, TimeSpan.FromSeconds(3), span);
                Logger.Info($"CalendarSyncService started (every {minutes} min) -> {settings.CalendarUrl}");
            }
            catch (Exception ex) { try { Logger.Warn($"CalendarSyncService Start failed: {ex.Message}"); } catch { } }
        }

        public static void Stop()
        {
            lock (_lock) { _running = false; }
            try { _timer?.Dispose(); _timer = null; } catch { }
            try { _http?.Dispose(); _http = null; } catch { }
        }

        public static void Restart()
        {
            Stop();
            Start();
        }

        // Ручной проход (диалог импорта, Restart, вкл/выкл). Потокобезопасно.
        public static async Task<int> SyncNowAsync()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return 0;
            try
            {
                var settings = TodoStore.LoadSettings();
                var url = (settings.CalendarUrl ?? "").Trim();
                if (string.IsNullOrWhiteSpace(url)) return 0;
                if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
                    url = "https://" + url.Substring(9);

                HttpClient http = _http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
                bool own = _http == null;
                string ics;
                try
                {
                    var resp = await http.GetAsync(url);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Logger.Warn($"CalendarSyncService: HTTP {(int)resp.StatusCode} from {url}");
                        return 0;
                    }
                    ics = await resp.Content.ReadAsStringAsync();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"CalendarSyncService download failed: {ex.Message}");
                    return 0;
                }
                finally
                {
                    if (own) try { http.Dispose(); } catch { }
                }

                var events = IcsParser.ParseText(ics);
                var existing = new HashSet<string>(
                    TodoStore.Load()
                        .Where(x => x.Source == "calendar" && !string.IsNullOrEmpty(x.CalendarUid))
                        .Select(x => x.CalendarUid!),
                    StringComparer.OrdinalIgnoreCase);
                int added = 0;
                foreach (var ev in events)
                {
                    if (string.IsNullOrWhiteSpace(ev.Summary) || ev.Start == default) continue;
                    if (string.IsNullOrWhiteSpace(ev.Uid)) continue;
                    if (existing.Contains(ev.Uid)) continue;
                    // Будущее в прошлом и давно просрочено — не мусорим список
                    // (напоминалки всё равно молчат из-за grace-периода 24 ч).
                    try
                    {
                        TodoStore.Add(text: ev.Summary, source: "calendar", dueAt: ev.Start, calendarUid: ev.Uid);
                        existing.Add(ev.Uid);
                        added++;
                    }
                    catch { }
                }

                if (added > 0)
                    try { Logger.Info($"CalendarSyncService added {added} events"); } catch { }

                // Фиксируем время успешного синка.
                var s = TodoStore.LoadSettings();
                s.CalendarLastSync = DateTime.Now;
                try { TodoStore.SaveSettings(s); } catch { }

                try { OnCalendarSynced?.Invoke(added); } catch { }
                return added;
            }
            catch (Exception ex) { try { Logger.Warn($"CalendarSyncService sync failed: {ex.Message}"); } catch { } return 0; }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        public static DateTime? LastSync()
        {
            try { return TodoStore.LoadSettings().CalendarLastSync; } catch { return null; }
        }
    }
}