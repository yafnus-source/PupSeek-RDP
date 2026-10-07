using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // Опрос web-ресурсов даша Z (ряд WEB): HTTP-статус + время ответа.
    // Паттерн как у ZabbixAgentPollerService (таймер, семафор, первый прогон
    // через 10 сек после старта). Крах = 2 провала подряд (защита от одиночных
    // глитчей и флэпов); восстановление фиксируем Info-событием.
    //
    // События краха/восстановления кладём в EventDashboardStore — дальше
    // работает штатный W-конвейер (даш W, бейджи книги). О баллуне заботится
    // подписчик OnWebDown (RdpManagerForm) — как OnCritical у EventPoller.
    public static class WebMonitorPollerService
    {
        private static System.Threading.Timer? _timer;
        private static readonly object _lock = new object();
        private static bool _running;
        private static CancellationTokenSource? _cts;

        // Один HttpClient на всех (таймаут — на каждую проверку отдельно через CTS).
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        // Состояние краха: счётчик последовательных провалов + флаг "лежит".
        private static readonly Dictionary<string, int> _fails = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _down = new(StringComparer.OrdinalIgnoreCase);

        public static event Action<WebPoint>? OnWebDown;
        public static event Action<WebPoint>? OnWebUp;
        public static event Action<string, int, int>? OnProgress; // url, current, total

        public static void Start()
        {
            lock (_lock)
            {
                if (_timer != null) return;
                var settings = WebMonitorStore.LoadSettings();
                int mins = Math.Max(5, settings.IntervalMinutes);
                _cts = new CancellationTokenSource();
                _timer = new System.Threading.Timer(async _ => await PollOnceAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(mins));
                Logger.Info($"WebMonitor Poller: started every {mins} min");
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                try { _cts?.Cancel(); } catch { }
                _cts?.Dispose(); _cts = null;
                _timer?.Dispose(); _timer = null;
                Logger.Info("WebMonitor Poller: stopped");
            }
        }

        public static void Restart() { Stop(); Start(); }

        public static async Task PollOnceAsync()
        {
            if (_running) return;
            _running = true;
            try
            {
                var checks = WebMonitorStore.LoadUrls().Where(c => c.Enabled).ToList();
                if (checks.Count == 0) { Logger.Info("WebMonitor Poller: no urls"); return; }
                // Чистим стейт удалённых из списка URL, чтобы словари не росли.
                lock (_lock)
                {
                    var alive = new HashSet<string>(checks.Select(c => c.Url.Trim()), StringComparer.OrdinalIgnoreCase);
                    foreach (var k in _fails.Keys.Where(k => !alive.Contains(k)).ToList()) _fails.Remove(k);
                    _down.RemoveWhere(u => !alive.Contains(u));
                }
                var settings = WebMonitorStore.LoadSettings();
                var points = new List<WebPoint>();
                var sem = new SemaphoreSlim(Math.Max(1, settings.Concurrency));
                var tasks = new List<Task>();
                int idx = 0;
                foreach (var check in checks)
                {
                    int cur = Interlocked.Increment(ref idx);
                    try { OnProgress?.Invoke(check.Url, cur, checks.Count); } catch { }
                    await sem.WaitAsync(_cts?.Token ?? CancellationToken.None);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            int timeout = check.TimeoutMs > 0 ? check.TimeoutMs : settings.TimeoutMs;
                            var pt = await CheckOnceAsync(check.Url, timeout, _cts?.Token ?? CancellationToken.None);
                            lock (points) points.Add(pt);
                            Logger.Info($"WebMonitor {check.Url} -> ok={pt.Ok} code={pt.StatusCode} {pt.Ms}ms {pt.Error}");
                        }
                        catch (Exception ex) { try { Logger.Warn($"WebMonitor {check.Url}: {ex.Message}"); } catch { } }
                        finally { sem.Release(); }
                    }));
                }
                await Task.WhenAll(tasks);
                if (points.Count > 0)
                {
                    WebMonitorStore.AddPoints(points);
                    ProcessTransitions(points);
                    Logger.Info($"WebMonitor Poller: +{points.Count} points from {checks.Count} urls");
                }
                try { OnProgress?.Invoke("", checks.Count, checks.Count); } catch { }
            }
            catch (Exception ex)
            {
                // v3.9.3D — при обрыве опроса гасим прогресс-бар в форме,
                // иначе он остаётся висеть навсегда.
                try { OnProgress?.Invoke("", 0, 0); } catch { }
                try { Logger.Warn($"WebMonitor PollOnce: {ex.Message}"); } catch { }
            }
            finally { _running = false; }
        }

        private static async Task<WebPoint> CheckOnceAsync(string url, int timeoutMs, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(Math.Max(1000, timeoutMs));
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                sw.Stop();
                int code = (int)resp.StatusCode;
                bool ok = code >= 200 && code <= 399;
                return new WebPoint
                {
                    Url = url,
                    Time = DateTime.Now,
                    Ok = ok,
                    StatusCode = code,
                    Ms = sw.ElapsedMilliseconds,
                    Error = ok ? "" : $"HTTP {code}"
                };
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return new WebPoint { Url = url, Time = DateTime.Now, Ok = false, StatusCode = 0, Ms = sw.ElapsedMilliseconds, Error = "таймаут" };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new WebPoint { Url = url, Time = DateTime.Now, Ok = false, StatusCode = 0, Ms = sw.ElapsedMilliseconds, Error = ShortError(ex) };
            }
        }

        // Переходы состояний (фронты): 2 провала подряд = крах, успех после
        // краха = восстановление. События — по одному на переход, дублей нет.
        private static void ProcessTransitions(List<WebPoint> points)
        {
            foreach (var pt in points)
            {
                bool isDown;
                lock (_lock)
                {
                    if (pt.Ok)
                    {
                        _fails[pt.Url] = 0;
                        isDown = _down.Remove(pt.Url);
                    }
                    else
                    {
                        _fails.TryGetValue(pt.Url, out int f);
                        _fails[pt.Url] = f + 1;
                        isDown = false;
                        if (_fails[pt.Url] >= 2 && _down.Add(pt.Url))
                            isDown = true; // фронт краха — только здесь
                        else if (_down.Contains(pt.Url))
                            isDown = false; // уже лежит — молчим
                    }
                }
                if (pt.Ok && isDown)
                {
                    var ev = new DashboardEvent
                    {
                        Time = pt.Time,
                        Level = "Info",
                        Source = "WebMonitor",
                        EventId = 1002,
                        Computer = HostOf(pt.Url),
                        Message = $"{pt.Url} — снова отвечает (HTTP {pt.StatusCode} за {pt.Ms}мс)",
                        LogName = "WebMonitor"
                    };
                    EventDashboardStore.AddEvents(new[] { ev });
                    try { OnWebUp?.Invoke(pt); } catch { }
                    Logger.Info($"WebMonitor UP: {pt.Url}");
                }
                else if (!pt.Ok && isDown)
                {
                    string detail = pt.StatusCode > 0 ? $"HTTP {pt.StatusCode} за {pt.Ms}мс" : $"{pt.Error} ({pt.Ms}мс)";
                    var ev = new DashboardEvent
                    {
                        Time = pt.Time,
                        Level = "Critical",
                        Source = "WebMonitor",
                        EventId = 1001,
                        Computer = HostOf(pt.Url),
                        Message = $"{pt.Url} — {detail}",
                        LogName = "WebMonitor"
                    };
                    EventDashboardStore.AddEvents(new[] { ev });
                    try { OnWebDown?.Invoke(pt); } catch { }
                    Logger.Info($"WebMonitor DOWN: {pt.Url} ({detail})");
                }
            }
        }

        private static string HostOf(string url)
        {
            try
            {
                var u = new Uri(url.Trim());
                if (!string.IsNullOrEmpty(u.Host)) return u.Host;
            }
            catch { }
            return url.Trim();
        }

        private static string ShortError(Exception ex)
        {
            var m = (ex as HttpRequestException)?.Message ?? ex.Message ?? "";
            var nl = m.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) m = m.Substring(0, nl);
            m = m.Trim();
            return m.Length > 120 ? m.Substring(0, 120) + "…" : m;
        }
    }
}
