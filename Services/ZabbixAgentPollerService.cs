using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    public static class ZabbixAgentPollerService
    {
        private static System.Threading.Timer? _timer;
        private static readonly object _lock = new object();
        private static bool _running;
        private static CancellationTokenSource? _cts;

        public static event Action<List<ZabbixAgentData>>? OnNewData;
        public static event Action<string, int, int>? OnProgress; // host, current, total

        public static void Start()
        {
            lock (_lock)
            {
                if (_timer != null) return;
                var settings = ZabbixAgentStore.LoadSettings();
                int mins = Math.Max(5, settings.PollMinutes);
                _cts = new CancellationTokenSource();
                _timer = new System.Threading.Timer(async _ => await PollOnceAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(mins));
                Logger.Info($"ZabbixAgent Poller: started every {mins} min");
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                try { _cts?.Cancel(); } catch { }
                _cts?.Dispose(); _cts = null;
                _timer?.Dispose(); _timer = null;
                Logger.Info("ZabbixAgent Poller: stopped");
            }
        }

        public static void Restart() { Stop(); Start(); }

        // v4.7 — общий список целевых хостов даша Z (мониторинг + SNMP-метка
        // книги + ручные записи): переиспользуется пингом (HostPingService).
        public static List<string> GetTargetHosts()
        {
            try
            {
                var hosts = EventDashboardStore.GetMonitoredComputers();
                // Add hosts explicitly marked "Отслеживать по SNMP" even if not
                // in the monitored_computers list.
                var book = AddressBookStore.Load();
                var snmpHosts = book.Where(e => e.SnmpMonitored && !string.IsNullOrWhiteSpace(e.Computer))
                    .Select(e => e.Computer!.Trim()).ToList();
                // Дополнительные хосты из даша Z («Добавить запись», вне книги).
                var manual = ZabbixAgentStore.LoadManualHosts();
                var manualHosts = manual.Where(e => !string.IsNullOrWhiteSpace(e.Computer))
                    .Select(e => e.Computer!.Trim()).ToList();
                hosts = hosts.Union(snmpHosts, StringComparer.OrdinalIgnoreCase)
                    .Union(manualHosts, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return hosts;
            }
            catch { return new List<string>(); }
        }

        public static async Task PollOnceAsync()
        {
            if (_running) return;
            _running = true;
            try
            {
                var hosts = GetTargetHosts();
                if (hosts.Count == 0) { Logger.Info("ZabbixAgent Poller: no monitored hosts"); return; }
                var settings = ZabbixAgentStore.LoadSettings();
                var bookAll = AddressBookStore.Load();
                var manualAll = ZabbixAgentStore.LoadManualHosts();
                var bookById = bookAll.GroupBy(e => e.Computer ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                // Ручные записи даша Z имеют приоритет над книгой для одного хоста.
                foreach (var m in manualAll.Where(e => !string.IsNullOrWhiteSpace(e.Computer)))
                    bookById[m.Computer!.Trim()] = m;
                var results = new List<ZabbixAgentData>();
                var sem = new SemaphoreSlim(Math.Max(1, settings.Concurrency));
                var tasks = new List<Task>();
                int idx = 0;
                foreach (var host in hosts)
                {
                    int cur = System.Threading.Interlocked.Increment(ref idx);
                    try { OnProgress?.Invoke(host, cur, hosts.Count); } catch { }
                    await sem.WaitAsync(_cts?.Token ?? CancellationToken.None);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            bookById.TryGetValue(host, out var entry);
                            bool snmpFirst = entry?.SnmpMonitored == true;
                            string community = string.IsNullOrWhiteSpace(entry?.SnmpCommunity) ? settings.SnmpCommunity : entry!.SnmpCommunity!.Trim();
                            string version = string.IsNullOrWhiteSpace(entry?.SnmpVersion) ? settings.SnmpVersion : entry!.SnmpVersion!.Trim();
                            ushort port = (ushort)(entry?.SnmpPort is int p && p > 0 ? p : settings.SnmpPort);

                            var data = snmpFirst
                                ? await SnmpClient.CollectAsync(host, settings.SnmpTimeoutMs, community, version, port, _cts?.Token ?? CancellationToken.None)
                                : await ZabbixAgentClient.CollectAsync(host, settings.TimeoutMs, _cts?.Token ?? CancellationToken.None);

                            if (!data.AgentPing && snmpFirst && settings.SnmpEnabled)
                                data = await ZabbixAgentClient.CollectAsync(host, settings.TimeoutMs, _cts?.Token ?? CancellationToken.None);
                            else if (!data.AgentPing && !snmpFirst && settings.SnmpEnabled && settings.SnmpFallback)
                                data = await SnmpClient.CollectAsync(host, settings.SnmpTimeoutMs, community, version, port, _cts?.Token ?? CancellationToken.None);

                            lock (results) results.Add(data);
                            Logger.Info($"ZabbixAgent {host} [{data.Source}] -> ping={data.AgentPing} cpu={data.CpuLoad:0.0} memPfree={data.MemPFree:0.0} disks={data.Disks.Count} temp={(data.TempC.HasValue ? data.TempC.Value.ToString("0.0") : "n/a")}");
                        }
                        catch (Exception ex) { try { Logger.Warn($"ZabbixAgent {host}: {ex.Message}"); } catch { } }
                        finally { sem.Release(); }
                    }));
                }
                await Task.WhenAll(tasks);
                if (results.Count > 0)
                {
                    ZabbixAgentStore.AddPoints(results);
                    try { OnNewData?.Invoke(results); } catch { }
                    // v3.9.3 — алерты «мало места на диске» (баллун+звук).
                    try { HealthAlertService.CheckDisks(results, settings.DiskFreeGb, settings.DiskFreeAlert, settings.AlertSound); } catch { }
                    Logger.Info($"ZabbixAgent Poller: +{results.Count} points from {hosts.Count} hosts");
                }
                try { OnProgress?.Invoke("", hosts.Count, hosts.Count); } catch { }
            }
            catch (Exception ex)
            {
                // v3.9.3D — при обрыве опроса гасим прогресс-бар в форме.
                try { OnProgress?.Invoke("", 0, 0); } catch { }
                try { Logger.Warn($"ZabbixAgent PollOnce: {ex.Message}"); } catch { }
            }
            finally { _running = false; }
        }
    }
}
