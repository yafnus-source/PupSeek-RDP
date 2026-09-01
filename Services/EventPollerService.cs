using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    public static class EventPollerService
    {
        private static System.Threading.Timer? _timer;
        private static readonly object _lock = new object();
        private static bool _running;
        private static CancellationTokenSource? _cts;

        public static event Action<List<DashboardEvent>>? OnNewEvents;
        public static event Action<DashboardEvent>? OnCritical;

        public static void Start()
        {
            lock (_lock)
            {
                if (_timer != null) return;
                var settings = EventDashboardStore.LoadSettings();
                var mins = Math.Max(5, settings.PollIntervalMinutes);
                _cts = new CancellationTokenSource();
                _timer = new System.Threading.Timer(async _ => await PollOnceAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(mins));
                Logger.Info($"EventPoller: started every {mins} min");
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                try { _cts?.Cancel(); } catch { }
                _cts?.Dispose(); _cts = null;
                _timer?.Dispose(); _timer = null;
                Logger.Info("EventPoller: stopped");
            }
        }

        public static void Restart()
        {
            Stop(); Start();
        }

        public static async Task PollOnceAsync()
        {
            if (_running) return;
            _running = true;
            try
            {
                var hosts = EventDashboardStore.GetMonitoredComputers();
                if (hosts.Count == 0) { Logger.Info("EventPoller: no monitored hosts"); return; }
                var settings = EventDashboardStore.LoadSettings();
                var cutoff = DateTime.Now.AddDays(-settings.RetentionDays);
                var existing = EventDashboardStore.LoadEvents();
                var existingKeys = new HashSet<string>(existing.Select(e => $"{e.Computer}|{e.EventId}|{e.Time:O}|{e.Source}"));

                var allNew = new List<DashboardEvent>();
                foreach (var host in hosts)
                {
                    try
                    {
                        var events = await QueryHostAsync(host, cutoff, _cts?.Token ?? CancellationToken.None);
                        Logger.Info($"EventPoller: {host} -> {events.Count} events");
                        foreach (var ev in events)
                        {
                            var key = $"{ev.Computer}|{ev.EventId}|{ev.Time:O}|{ev.Source}";
                            if (!existingKeys.Contains(key))
                            {
                                allNew.Add(ev);
                                existingKeys.Add(key);
                            }
                        }
                    }
                    catch (Exception ex) { try { Logger.Warn($"Poll {host}: {ex.Message}"); } catch { } }
                }

                if (allNew.Count > 0)
                {
                    allNew = allNew.OrderByDescending(e => e.Time).ToList();
                    EventDashboardStore.AddEvents(allNew);
                    try { OnNewEvents?.Invoke(allNew); } catch { }
                    if (settings.NotifyCritical)
                    {
                        foreach (var ev in allNew.Where(e => string.Equals(e.Level, "Critical", StringComparison.OrdinalIgnoreCase)))
                        {
                            try { OnCritical?.Invoke(ev); } catch { }
                        }
                        if (settings.NotifyError)
                        {
                            foreach (var ev in allNew.Where(e => string.Equals(e.Level, "Error", StringComparison.OrdinalIgnoreCase)))
                            {
                                try { OnCritical?.Invoke(ev); } catch { }
                            }
                        }
                    }
                    Logger.Info($"EventPoller: +{allNew.Count} new events from {hosts.Count} hosts");
                }
                else
                {
                    Logger.Info($"EventPoller: 0 new events from {hosts.Count} hosts (cutoff {cutoff:yyyy-MM-dd HH:mm})");
                }
            }
            catch (Exception ex) { try { Logger.Warn($"PollOnce failed: {ex.Message}"); } catch { } }
            finally { _running = false; }
        }

        private static async Task<List<DashboardEvent>> QueryHostAsync(string computer, DateTime cutoff, CancellationToken ct)
        {
            var clean = computer.Trim();
            bool isLocal = string.Equals(clean, ".", StringComparison.OrdinalIgnoreCase) || string.Equals(clean, "localhost", StringComparison.OrdinalIgnoreCase) || string.Equals(clean, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            // Primary: EventLogSession (fast, per-log, no WMI hang). Your PowerShell test hung because WMI scanned ALL logs.
            try
            {
                long diffMs = (long)(DateTime.UtcNow - cutoff.ToUniversalTime()).TotalMilliseconds;
                if (diffMs < 0) diffMs = 172800000;
                if (diffMs > 172800000) diffMs = 172800000;
                string xpath = $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {diffMs}]]]";
                var logs = new[] { "System", "Application" };
                var task = Task.Run(() =>
                {
                    var tmp = new List<DashboardEvent>();
                    foreach (var log in logs)
                    {
                        EventLogSession? session = null;
                        EventLogQuery? query = null;
                        EventLogReader? reader = null;
                        try
                        {
                            query = new EventLogQuery(log, PathType.LogName, xpath);
                            if (!isLocal) { try { session = new EventLogSession(clean); query.Session = session; } catch { query.Session = null; } }
                            reader = new EventLogReader(query);
                            for (EventRecord? rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
                            {
                                try
                                {
                                    if (rec.TimeCreated == null) continue;
                                    if (rec.TimeCreated.Value.ToLocalTime() < cutoff) continue;
                                    int code = rec.Id;
                                    string level = rec.Level == 1 ? "Critical" : "Error";
                                    var criticalIds = new HashSet<int> { 41, 55, 6008, 1001 };
                                    if (criticalIds.Contains(code)) level = "Critical";
                                    string msg = "";
                                    try { msg = rec.FormatDescription() ?? ""; } catch { try { msg = rec.ToString() ?? ""; } catch { } }
                                    tmp.Add(new DashboardEvent { Time = rec.TimeCreated.Value.ToLocalTime(), Level = level, Source = rec.ProviderName ?? "", EventId = code, Computer = clean, Message = msg.Trim(), LogName = log, Notified = false });
                                    if (tmp.Count >= 200) break;
                                }
                                finally { rec.Dispose(); }
                                if (ct.IsCancellationRequested) break;
                            }
                        }
                        catch (Exception ex) { Logger.Warn($"EventLog poll {clean}/{log}: {ex.Message}"); }
                        finally { try { reader?.Dispose(); } catch { } try { session?.Dispose(); } catch { } }
                    }
                    return tmp;
                }, ct);
                var timeout = Task.Delay(TimeSpan.FromSeconds(15), ct);
                var winner = await Task.WhenAny(task, timeout);
                if (winner == task)
                {
                    var res = await task;
                    if (res.Any()) return res.OrderByDescending(e => e.Time).Take(100).ToList();
                    Logger.Info($"EventLog poll {clean}: 0 events, trying WMI fallback");
                }
                else Logger.Warn($"EventLog poll {clean}: timeout 15s");
            }
            catch (Exception ex) { Logger.Warn($"EventLog poll {clean} outer: {ex.Message}"); }

            // Fallback: WMI per-log (narrow, avoids full scan that hangs)
            try
            {
                string wmiCutoff = ManagementDateTimeConverter.ToDmtfDateTime(cutoff);
                var tmp = new List<DashboardEvent>();
                var logs = new[] { "System", "Application" };
                foreach (var log in logs)
                {
                    string wql = $"SELECT TimeGenerated, EventCode, SourceName, Type, Message, Logfile FROM Win32_NTLogEvent WHERE Logfile='{log}' AND Type=1 AND TimeGenerated >= '{wmiCutoff}'";
                    ManagementScope scope = isLocal ? new ManagementScope(@"\\.\root\cimv2", new ConnectionOptions { Timeout = TimeSpan.FromSeconds(8), Authentication = AuthenticationLevel.PacketPrivacy }) : new ManagementScope($"\\\\{clean}\\root\\cimv2", new ConnectionOptions { Timeout = TimeSpan.FromSeconds(8), Authentication = AuthenticationLevel.PacketPrivacy });
                    var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
                    searcher.Options.Timeout = TimeSpan.FromSeconds(8);
                    searcher.Options.ReturnImmediately = true;
                    var t = Task.Run(() =>
                    {
                        var local = new List<DashboardEvent>();
                        try
                        {
                            using var results = searcher.Get();
                            int cnt = 0;
                            foreach (ManagementObject mo in results)
                            {
                                try
                                {
                                    int code = 0; var co = mo["EventCode"]; if (co != null) int.TryParse(co.ToString(), out code);
                                    string level = new HashSet<int> { 41, 55, 6008, 1001 }.Contains(code) ? "Critical" : "Error";
                                    var timeObj = mo["TimeGenerated"]?.ToString();
                                    DateTime time = DateTime.Now;
                                    if (!string.IsNullOrEmpty(timeObj)) try { time = ManagementDateTimeConverter.ToDateTime(timeObj); } catch { }
                                    local.Add(new DashboardEvent { Time = time, Level = level, Source = mo["SourceName"]?.ToString() ?? "", EventId = code, Computer = clean, Message = (mo["Message"]?.ToString() ?? "").Trim(), LogName = log, Notified = false });
                                    if (++cnt >= 50) break;
                                }
                                finally { mo.Dispose(); }
                                if (ct.IsCancellationRequested) break;
                            }
                        }
                        catch (Exception ex) { Logger.Warn($"WMI poll {clean}/{log}: {ex.Message}"); }
                        return local;
                    }, ct);
                    var to = Task.Delay(TimeSpan.FromSeconds(10), ct);
                    var w = await Task.WhenAny(t, to);
                    if (w == t) tmp.AddRange(await t);
                    else Logger.Warn($"WMI poll {clean}/{log}: timeout 10s");
                    if (ct.IsCancellationRequested) break;
                }
                if (tmp.Any()) return tmp.OrderByDescending(e => e.Time).Take(100).ToList();
            }
            catch (Exception ex) { Logger.Warn($"WMI fallback {clean}: {ex.Message}"); }
            return new List<DashboardEvent>();
        }
    }
}
