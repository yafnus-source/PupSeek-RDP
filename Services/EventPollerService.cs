using System;
using System.Collections.Generic;
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
                if (hosts.Count == 0) return;
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
            }
            catch (Exception ex) { try { Logger.Warn($"PollOnce failed: {ex.Message}"); } catch { } }
            finally { _running = false; }
        }

        private static async Task<List<DashboardEvent>> QueryHostAsync(string computer, DateTime cutoff, CancellationToken ct)
        {
            // Query last 2 days, Error (Type=1) and Critical handling via Level? Win32_NTLogEvent.Type: 1=Error, 2=Warning. Critical is Type=1 with EventCode in critical list but we treat all Error as candidates and filter by Log.
            // Simpler: WHERE Type=1 (Error) and TimeGenerated recent.
            // Win32_NTLogEvent TimeGenerated is datetime string.
            var list = new List<DashboardEvent>();
            // WMI datetime format: yyyymmddHHMMSS.mmmmmm+UUU ; cutoff as WMI string
            string wmiCutoff = ManagementDateTimeConverter.ToDmtfDateTime(cutoff);
            string wql = $"SELECT TimeGenerated, EventCode, SourceName, Type, Message, Logfile FROM Win32_NTLogEvent WHERE Type=1 AND TimeGenerated >= '{wmiCutoff}'";

            // Use 30s timeout per host (longer than normal WMI 6s)
            var scope = new ManagementScope($"\\\\{computer.Trim()}\\root\\cimv2", new ConnectionOptions { Timeout = TimeSpan.FromSeconds(10), Authentication = AuthenticationLevel.PacketPrivacy });
            var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
            var task = Task.Run(() =>
            {
                var tmp = new List<DashboardEvent>();
                try
                {
                    using var results = searcher.Get();
                    foreach (ManagementObject mo in results)
                    {
                        try
                        {
                            var typeObj = mo["Type"]?.ToString();
                            // Type: convert to level: 1=Error, treat as Error (Critical not distinct in Win32_NTLogEvent, but EventCode in critical set could be mapped)
                            string level = "Error";
                            var codeObj = mo["EventCode"];
                            int code = 0;
                            if (codeObj != null) int.TryParse(codeObj.ToString(), out code);
                            // Known critical IDs
                            var criticalIds = new HashSet<int> { 41, 55, 6008, 1001 };
                            if (criticalIds.Contains(code)) level = "Critical";

                            var timeObj = mo["TimeGenerated"]?.ToString();
                            DateTime time = DateTime.Now;
                            if (!string.IsNullOrEmpty(timeObj))
                            {
                                try { time = ManagementDateTimeConverter.ToDateTime(timeObj); } catch { }
                            }
                            var ev = new DashboardEvent
                            {
                                Time = time,
                                Level = level,
                                Source = mo["SourceName"]?.ToString() ?? "",
                                EventId = code,
                                Computer = computer.Trim(),
                                Message = (mo["Message"]?.ToString() ?? "").Trim(),
                                LogName = mo["Logfile"]?.ToString() ?? "",
                                Notified = false
                            };
                            // Restore decoded/fallback if exists in cache? keep empty, decoder will fill later
                            tmp.Add(ev);
                        }
                        finally { mo.Dispose(); }
                    }
                }
                catch (Exception ex) { Logger.Warn($"WMI poll {computer}: {ex.Message}"); }
                return tmp;
            }, ct);

            var timeout = Task.Delay(TimeSpan.FromSeconds(30), ct);
            var winner = await Task.WhenAny(task, timeout);
            if (winner != task) return list;
            var res = await task;
            // sort and take top 100 per host to avoid flood
            return res.OrderByDescending(e => e.Time).Take(100).ToList();
        }
    }
}
