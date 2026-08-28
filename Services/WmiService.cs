using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace ShadowRDP.Services
{
    // WMI helpers with hard timeout per call. Mirror of $wmiWithTimeout from
    // the PowerShell version — without timeouts, Get-WmiObject on a hung
    // host blocks forever on RPC.
    //
    // All methods are async + cancellable. Use the provided CancellationToken
    // to abort outstanding work (e.g. user clicked Cancel).
    //
    // Connection options:
    //   - Timeout: 6s (matches PS). Enough for healthy hosts on LAN.
    //   - Authentication: PacketPrivacy (RPC level 6) — required by some
    //     target configs and harmless otherwise.
    public static class WmiService
    {
        private const int DefaultTimeoutSec = 6;

        // Win32_ComputerSystem — the only fields the address book cares about.
        public sealed class ComputerSystem
        {
            public string Name { get; set; } = "";
            public string UserName { get; set; } = "";
            public ulong TotalPhysicalMemory { get; set; }
        }

        // Win32_NetworkAdapterConfiguration where IPEnabled=true. IPs from all
        // adapters concatenated, comma-separated.
        public sealed class NetworkConfig
        {
            public List<string> IPv4Addresses { get; set; } = new();
        }

        // Query Win32_ComputerSystem on a remote host. Returns null on timeout
        // or error — caller treats null as "couldn't reach".
        public static async Task<ComputerSystem?> GetComputerSystemAsync(string computer, CancellationToken ct = default)
        {
            return await QueryWithTimeoutAsync(computer, "SELECT * FROM Win32_ComputerSystem", ct, mo =>
            {
                var cs = new ComputerSystem();
                cs.Name = mo["Name"]?.ToString() ?? "";
                cs.UserName = mo["UserName"]?.ToString() ?? "";
                if (mo["TotalPhysicalMemory"] != null &&
                    ulong.TryParse(mo["TotalPhysicalMemory"].ToString(), out var mem))
                {
                    cs.TotalPhysicalMemory = mem;
                }
                return cs;
            });
        }

        // Query Win32_NetworkAdapterConfiguration (IPEnabled=true) and collect
        // all IPv4 addresses.
        public static async Task<NetworkConfig?> GetNetworkConfigAsync(string computer, CancellationToken ct = default)
        {
            return await QueryCollectionWithTimeoutAsync(computer,
                "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=true", ct, mo =>
            {
                var addrs = mo["IPAddress"] as string[];
                if (addrs == null) return null;
                var ipv4 = addrs.Where(a => System.Net.IPAddress.TryParse(a, out var ip) &&
                                            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                return ipv4.ToList();
            }).ContinueWith(t =>
            {
                if (t.Result == null) return null;
                var cfg = new NetworkConfig();
                foreach (var list in t.Result) cfg.IPv4Addresses.AddRange(list);
                return cfg;
            }, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        // === Core: run a WMI query with hard timeout ===
        //
        // We can't pass a CancellationToken directly to ManagementObjectSearcher
        // (it doesn't accept one). Instead we wrap the query in Task.Run + add
        // a ContinueWith that fires after the timeout — if the searcher hasn't
        // completed by then, we leave it running (it'll eventually time out
        // internally or fail) and return null. Cancellation via ct aborts the
        // wait early.
        private static async Task<T?> QueryWithTimeoutAsync<T>(
            string computer, string wql, CancellationToken ct,
            Func<ManagementObject, T> project) where T : class
        {
            try
            {
                // ManagementScope doesn't implement IDisposable — don't `using` it.
                var scope = CreateScope(computer);
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
                var queryTask = Task.Run(() =>
                {
                    try
                    {
                        using var results = searcher.Get();
                        foreach (ManagementObject mo in results)
                        {
                            try { return project(mo); }
                            finally { mo.Dispose(); }
                        }
                        return null;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"WMI query failed on {computer}: {ex.Message}");
                        return null;
                    }
                }, ct);

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(DefaultTimeoutSec), ct);
                var winner = await Task.WhenAny(queryTask, timeoutTask);
                if (winner != queryTask) return null;
                return await queryTask;
            }
            catch (Exception ex)
            {
                Logger.Warn($"WMI exception on {computer}: {ex.Message}");
                return null;
            }
        }

        private static async Task<List<T>?> QueryCollectionWithTimeoutAsync<T>(
            string computer, string wql, CancellationToken ct,
            Func<ManagementObject, T?> project) where T : class
        {
            try
            {
                var scope = CreateScope(computer);
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
                var queryTask = Task.Run(() =>
                {
                    try
                    {
                        var list = new List<T>();
                        using var results = searcher.Get();
                        foreach (ManagementObject mo in results)
                        {
                            try
                            {
                                var item = project(mo);
                                if (item != null) list.Add(item);
                            }
                            finally { mo.Dispose(); }
                        }
                        return list;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"WMI collection query failed on {computer}: {ex.Message}");
                        return new List<T>();
                    }
                }, ct);

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(DefaultTimeoutSec), ct);
                var winner = await Task.WhenAny(queryTask, timeoutTask);
                if (winner != queryTask) return null;
                return await queryTask;
            }
            catch (Exception ex)
            {
                Logger.Warn($"WMI collection exception on {computer}: {ex.Message}");
                return null;
            }
        }

        private static ManagementScope CreateScope(string computer)
        {
            var path = new ManagementPath($"\\\\{computer.Trim()}\\root\\cimv2");
            var options = new ConnectionOptions
            {
                Timeout = TimeSpan.FromSeconds(DefaultTimeoutSec),
                Authentication = AuthenticationLevel.PacketPrivacy
            };
            return new ManagementScope(path, options);
        }
    }
}
