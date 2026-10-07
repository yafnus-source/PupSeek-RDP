using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // v4.7 — ICMP-пинг хостов даша Z (колонка «Пинг»: OK/FAIL).
    // Опрос раз в час (первый — через ~30с после старта), результаты живут
    // в памяти + hostping.json, форма обновляется через событие OnPingUpdate.
    public static class HostPingService
    {
        private static System.Threading.Timer? _timer;
        private static readonly object _lock = new object();
        private static bool _running;
        private static readonly Dictionary<string, HostPingPoint> _last =
            new Dictionary<string, HostPingPoint>(StringComparer.OrdinalIgnoreCase);

        public static event Action? OnPingUpdate;

        public static bool TryGet(string host, out HostPingPoint pt)
        {
            lock (_lock) return _last.TryGetValue(host ?? "", out pt!);
        }

        public static void Start()
        {
            lock (_lock)
            {
                if (_timer != null) return;
                Load();
                _timer = new System.Threading.Timer(async _ => await PingNowAsync(),
                    null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
                Logger.Info("HostPing: started every 60 min (first in ~30s)");
            }
        }

        public static void Stop()
        {
            lock (_lock)
            {
                try { _timer?.Dispose(); } catch { }
                _timer = null;
                Logger.Info("HostPing: stopped");
            }
        }

        public static void Restart() { Stop(); Start(); }

        // notify=false — для тестов: без баллунов/звука (фейки харнесса
        // не должны пугать пользователя несуществующими серверами).
        public static async Task PingNowAsync(List<string>? onlyHosts = null, bool notify = true)
        {
            if (_running) return;
            _running = true;
            try
            {
                // onlyHosts — для тестов (харнесс пингует фейковые имена,
                // не трогая реальные 11 серверов пользователя).
                var hosts = onlyHosts ?? ZabbixAgentPollerService.GetTargetHosts();
                if (hosts.Count == 0) { Logger.Info("HostPing: no monitored hosts"); return; }
                var sem = new SemaphoreSlim(8);
                var tasks = new List<Task>();
                foreach (var host in hosts)
                {
                    await sem.WaitAsync();
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var pt = await PingOneAsync(host);
                            lock (_lock) _last[host] = pt;
                        }
                        catch { }
                        finally { sem.Release(); }
                    }));
                }
                await Task.WhenAll(tasks);
                // v3.9.3BD — в тестовом режиме (onlyHosts) файл не пишем:
                // иначе фейки харнесса загрязняют hostping.json пользователя.
                if (onlyHosts == null) Save();
                else { try { Logger.Info("HostPing: тестовый прогон, файл не трогаем"); } catch { } }
                try { OnPingUpdate?.Invoke(); } catch { }
                // v3.9.3 — алерты «нет пинга» (баллун+звук, раз на инцидент).
                if (notify)
                {
                    try
                    {
                        var s = ZabbixAgentStore.LoadSettingsCached();
                        Dictionary<string, HostPingPoint> snap;
                        lock (_lock) snap = new Dictionary<string, HostPingPoint>(_last, StringComparer.OrdinalIgnoreCase);
                        HealthAlertService.CheckPing(snap, s.PingAlert, s.AlertSound);
                    }
                    catch { }
                }
                int ok = 0;
                lock (_lock) ok = _last.Values.Count(p => p.Ok);
                Logger.Info($"HostPing: +{hosts.Count} hosts, OK: {ok}");
            }
            catch (Exception ex) { try { Logger.Warn($"HostPing: {ex.Message}"); } catch { } }
            finally { _running = false; }
        }

        private static async Task<HostPingPoint> PingOneAsync(string host)
        {
            var pt = new HostPingPoint { Host = host, Ok = false, Ms = -1, Time = DateTime.Now };
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 2000);
                if (reply != null && reply.Status == IPStatus.Success)
                {
                    pt.Ok = true;
                    pt.Ms = reply.RoundtripTime;
                }
                pt.Time = DateTime.Now;
            }
            catch { pt.Time = DateTime.Now; }
            return pt;
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(AppPaths.HostPingPath)) return;
                var txt = File.ReadAllText(AppPaths.HostPingPath, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<HostPingPoint>>(txt);
                if (list == null) return;
                lock (_lock)
                    foreach (var p in list)
                        if (!string.IsNullOrWhiteSpace(p.Host)) _last[p.Host] = p;
            }
            catch { }
        }

        private static void Save()
        {
            try
            {
                List<HostPingPoint> list;
                lock (_lock) list = _last.Values.ToList();
                Directory.CreateDirectory(AppPaths.DataDir);
                var txt = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(AppPaths.HostPingPath, txt, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
