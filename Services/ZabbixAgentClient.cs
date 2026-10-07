using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    public static class ZabbixAgentClient
    {
        private const int DefaultPort = 10050;

        public static async Task<string> QueryRawAsync(string host, string key, int timeoutMs, CancellationToken ct = default)
        {
            string clean = host.Trim();
            if (clean.Contains(":")) clean = clean.Split(':')[0];
            // VPN-aware: try host as-is, then try resolves via AddressBook IP if available
            var tryHosts = new List<string> { clean };
            try
            {
                var entries = AddressBookStore.Load();
                var match = entries.FirstOrDefault(e => string.Equals(e.Computer, clean, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrWhiteSpace(match.IPs))
                {
                    var ip = match.IPs.Split(',')[0].Trim();
                    if (!string.IsNullOrWhiteSpace(ip) && !string.Equals(ip, clean, StringComparison.OrdinalIgnoreCase))
                        tryHosts.Add(ip);
                }
            }
            catch { }
            Exception? lastEx = null;
            foreach (var h in tryHosts.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { return await QuerySingleHostAsync(h, key, timeoutMs, ct); }
                catch (Exception ex) { lastEx = ex; Logger.Warn($"Zabbix {h} {key}: {ex.Message}"); }
            }
            throw lastEx ?? new IOException("no host tried");
        }

        private static async Task<string> QuerySingleHostAsync(string host, string key, int timeoutMs, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(host, DefaultPort);
            var timeout = Task.Delay(timeoutMs, cts.Token);
            var winner = await Task.WhenAny(connectTask, timeout);
            if (winner != connectTask) throw new TimeoutException($"connect {host}:{DefaultPort} timeout");
            await connectTask;
            using var stream = tcp.GetStream();
            stream.ReadTimeout = timeoutMs;
            stream.WriteTimeout = timeoutMs;

            // ZBXD protocol: header(5) + keylen(8, little-endian) + key
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] req = new byte[13 + keyBytes.Length];
            req[0] = (byte)'Z'; req[1] = (byte)'B'; req[2] = (byte)'X'; req[3] = (byte)'D'; req[4] = 1;
            BitConverter.GetBytes((long)keyBytes.Length).CopyTo(req, 5);
            keyBytes.CopyTo(req, 13);
            await stream.WriteAsync(req, 0, req.Length, cts.Token);
            // Read all available - handle both ZBXD header and plain text (old agents return plain "1")
            var buf = new MemoryStream();
            var tmp = new byte[8192];
            // Give agent time to respond
            var readTask = stream.ReadAsync(tmp, 0, tmp.Length, cts.Token);
            var delay = Task.Delay(timeoutMs, cts.Token);
            var rWinner = await Task.WhenAny(readTask, delay);
            if (rWinner != readTask) throw new TimeoutException($"read {host} {key} timeout");
            int n = await readTask;
            if (n == 0) throw new IOException("empty response (agent closed, check Server whitelist or VPN)");
            buf.Write(tmp, 0, n);
            // If starts with ZBXD, parse header
            byte[] first = buf.ToArray();
            if (first.Length >= 13 && first[0] == (byte)'Z' && first[1] == (byte)'B' && first[2] == (byte)'X' && first[3] == (byte)'D')
            {
                long len = BitConverter.ToInt64(first, 5);
                if (len < 0 || len > 16 * 1024 * 1024) throw new IOException($"bad len {len}");
                // Already have first 13 + maybe part of data
                int haveData = (int)(first.Length - 13);
                var data = new byte[len];
                if (haveData > 0) Array.Copy(first, 13, data, 0, Math.Min(haveData, (int)len));
                int off = haveData;
                while (off < len)
                {
                    int r = await stream.ReadAsync(data, off, (int)(len - off), cts.Token);
                    if (r == 0) break;
                    off += r;
                }
                string resp = Encoding.UTF8.GetString(data, 0, (int)Math.Min(off, len)).Trim();
                return resp;
            }
            else
            {
                // Plain text - read until EOF (agent closes after response)
                // We already have n bytes, continue reading until close or timeout
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    if (!tcp.Connected) break;
                    if (stream.DataAvailable)
                    {
                        int r = await stream.ReadAsync(tmp, 0, tmp.Length, cts.Token);
                        if (r == 0) break;
                        buf.Write(tmp, 0, r);
                    }
                    else
                    {
                        await Task.Delay(50, cts.Token);
                        if (buf.Length > 0 && !stream.DataAvailable) break;
                    }
                    if (buf.Length > 0 && n < 13) break; // plain short response already complete
                }
                string resp = Encoding.UTF8.GetString(buf.ToArray()).Trim();
                // Plain may contain ZBXD header at start but we already handled; else plain value like "1" or JSON
                if (resp.StartsWith("ZBXD", StringComparison.Ordinal)) resp = resp.Substring(4).Trim();
                return resp;
            }
        }

        public static async Task<Dictionary<string, string>> QueryBatchAsync(string host, IEnumerable<string> keys, int timeoutMs, CancellationToken ct = default)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in keys)
            {
                try
                {
                    string v = await QueryRawAsync(host, k, timeoutMs, ct);
                    dict[k] = v;
                }
                catch (Exception ex)
                {
                    dict[k] = $"ERR:{ex.Message}";
                }
                if (ct.IsCancellationRequested) break;
            }
            return dict;
        }

        public static async Task<ZabbixAgentData> CollectAsync(string host, int timeoutMs, CancellationToken ct = default)
        {
            var data = new ZabbixAgentData { Host = host, Time = DateTime.Now };
            try
            {
                // Batch 1: core
                var coreKeys = new[]
                {
                    "agent.ping",
                    "agent.version",
                    "system.cpu.util",
                    "vm.memory.size[total]",
                    "vm.memory.size[available]",
                    "system.uptime"
                };
                var core = await QueryBatchAsync(host, coreKeys, timeoutMs, ct);
                string ping = core.TryGetValue("agent.ping", out var pv) ? pv : "";
                data.AgentPing = ping.Trim() == "1";
                if (!data.AgentPing && ping.StartsWith("ERR:")) { data.Error = ping; return data; }
                data.AgentVersion = core.TryGetValue("agent.version", out var av) ? av : "";
                if (double.TryParse((core.TryGetValue("system.cpu.util", out var cv) ? cv : "").Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var cpu))
                    data.CpuLoad = Math.Clamp(cpu * 100, 0, 100); // cpu.util 0..1 -> 0..100%
                else if (core.TryGetValue("system.cpu.util", out var raw) && raw != "ZBX_NOTSUPPORTED")
                    data.CpuLoad = 0;
                if (long.TryParse(core.TryGetValue("vm.memory.size[total]", out var mt) ? mt : "", out var tot)) data.MemTotal = tot;
                if (long.TryParse(core.TryGetValue("vm.memory.size[available]", out var av2) ? av2 : "", out var avail)) data.MemAvailable = avail;
                if (data.MemTotal > 0) data.MemPFree = Math.Round((double)data.MemAvailable / data.MemTotal * 100, 1);
                // try pavailable directly for accuracy
                try
                {
                    string pfree = await QueryRawAsync(host, "vm.memory.size[pavailable]", timeoutMs, ct);
                    if (double.TryParse(pfree.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pf))
                        data.MemPFree = Math.Round(pf, 1);
                }
                catch { }
                if (long.TryParse(core.TryGetValue("system.uptime", out var up) ? up : "", out var upSec)) data.UptimeSec = upSec;

                if (!data.AgentPing) { data.Error = "agent.ping !=1"; return data; }

                // Network interfaces: discovery + stats
                try
                {
                    string disc = await QueryRawAsync(host, "net.if.discovery", timeoutMs, ct);
                    var ifaces = ParseNetIfDiscovery(disc);
                    foreach (var iface in ifaces)
                    {
                        try
                        {
                            string inS = await QueryRawAsync(host, $"net.if.in[{iface}]", timeoutMs, ct);
                            string outS = await QueryRawAsync(host, $"net.if.out[{iface}]", timeoutMs, ct);
                            string totalS = await QueryRawAsync(host, $"net.if.total[{iface}]", timeoutMs, ct);
                            long.TryParse(inS, out var inBps);
                            long.TryParse(outS, out var outBps);
                            long.TryParse(totalS, out var totalBps);
data.NetIfs.Add(new ZabbixAgentNetIf
                        {
                            Name = iface,
                            InBps = inBps,
                            OutBps = outBps
                            // TotalBps computed
                        });
                        }
                        catch { }
                        if (ct.IsCancellationRequested) break;
                    }
                }
                catch (Exception ex) { Logger.Warn($"Zabbix net.if.discovery {host}: {ex.Message}"); }

                // Services: discovery + state (only auto-start critical ones)
                try
                {
                    string disc = await QueryRawAsync(host, "service.discovery", timeoutMs, ct);
                    var svcNames = ParseServiceDiscovery(disc);
                    // Critical service names to monitor (case-insensitive)
                    var criticalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "wuauserv", "lanmanserver", "termservice", "spooler",
                        "w3svc", "mssqlserver", "sqlserveragent", "sqlbrowser",
                        "vss", "swprv", "eventlog", "rpcss", "dhcp", "dns",
                        "windefend", "mpssvc", "wscsvc", "wuaserv", "bits",
                        "trustedinstaller", "wuauserv", "ccmexec", "smsagenthost"
                    };
                    foreach (var svc in svcNames)
                    {
                        try
                        {
                            string state = await QueryRawAsync(host, $"service.info[{svc},state]", timeoutMs, ct);
                            string startup = await QueryRawAsync(host, $"service.info[{svc},startup]", timeoutMs, ct);
                            bool isCritical = criticalNames.Contains(svc);
                            // Only track critical auto-start services to reduce noise
                            if (isCritical || startup == "auto")
                            {
                                data.Services.Add(new ZabbixAgentService
                                {
                                    Name = svc,
                                    State = state,
                                    Startup = startup,
                                    IsCritical = isCritical
                                });
                            }
                        }
                        catch { }
                        if (ct.IsCancellationRequested) break;
                    }
                }
                catch (Exception ex) { Logger.Warn($"Zabbix service.discovery {host}: {ex.Message}"); }

                // Disks: discovery (должен идти ДО Disk I/O, чтобы mounts были известны)
                try
                {
                    string disc = await QueryRawAsync(host, "vfs.fs.discovery", timeoutMs, ct);
                    var mounts = ParseDiscovery(disc);
                    if (mounts.Count == 0) mounts.Add("C:");
                    foreach (var m in mounts)
                    {
                        try
                        {
                            string totalS = await QueryRawAsync(host, $"vfs.fs.size[{m},total]", timeoutMs, ct);
                            string freeS = await QueryRawAsync(host, $"vfs.fs.size[{m},free]", timeoutMs, ct);
                            string pfreeS = await QueryRawAsync(host, $"vfs.fs.size[{m},pfree]", timeoutMs, ct);
                            if (long.TryParse(totalS, out var t) && long.TryParse(freeS, out var f))
                            {
                                double pfree = 0;
                                double.TryParse(pfreeS.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out pfree);
                                data.Disks.Add(new ZabbixAgentDisk { Mount = m, Total = t, Free = f, PFree = Math.Round(pfree, 1) });
                            }
                        }
                        catch { }
                        if (ct.IsCancellationRequested) break;
                    }
                }
                catch (Exception ex) { Logger.Warn($"Zabbix discovery {host}: {ex.Message}"); }

                // Disk I/O: vfs.dev.read/write (использует уже собранные Disks)
                try
                {
                    var mounts = data.Disks.Select(d => d.Mount).ToList();
                    if (mounts.Count == 0) mounts.Add("C:");
                    foreach (var dev in mounts)
                    {
                        try
                        {
                            string rOps = await QueryRawAsync(host, $"vfs.dev.read[{dev},ops]", timeoutMs, ct);
                            string wOps = await QueryRawAsync(host, $"vfs.dev.write[{dev},ops]", timeoutMs, ct);
                            string rBps = await QueryRawAsync(host, $"vfs.dev.read[{dev},sectors]", timeoutMs, ct);
                            string wBps = await QueryRawAsync(host, $"vfs.dev.write[{dev},sectors]", timeoutMs, ct);
                            string rLat = await QueryRawAsync(host, $"vfs.dev.read[{dev},latency]", timeoutMs, ct);
                            string wLat = await QueryRawAsync(host, $"vfs.dev.write[{dev},latency]", timeoutMs, ct);
                            long.TryParse(rOps, out var readOps);
                            long.TryParse(wOps, out var writeOps);
                            long.TryParse(rBps, out var readBps);
                            long.TryParse(wBps, out var writeBps);
                            double.TryParse(rLat.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var readLat);
                            double.TryParse(wLat.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var writeLat);
                            data.DiskIo.Add(new ZabbixAgentDiskIo
                            {
                                Device = dev,
                                ReadOps = readOps,
                                WriteOps = writeOps,
                                ReadBps = readBps * 512, // sectors -> bytes
                                WriteBps = writeBps * 512,
                                ReadLatencyMs = readLat,
                                WriteLatencyMs = writeLat
                            });
                        }
                        catch { }
                        if (ct.IsCancellationRequested) break;
                    }
                }
                catch (Exception ex) { Logger.Warn($"Zabbix disk I/O {host}: {ex.Message}"); }

                // Temperature: try multiple keys without extra moves
                string[] tempKeys = { "sensor.temp", "system.hw.cpu[all,temp]", "system.hw.cpu[0,temp]" };
                foreach (var tk in tempKeys)
                {
                    try
                    {
                        string tv = await QueryRawAsync(host, tk, timeoutMs, ct);
                        if (tv == "ZBX_NOTSUPPORTED" || tv.StartsWith("ERR:")) continue;
                        if (double.TryParse(tv.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var tmp))
                        {
                            // sensor.temp may return array, take first
                            if (tv.TrimStart().StartsWith("[")) { try { var arr = JsonDocument.Parse(tv); if (arr.RootElement.ValueKind == JsonValueKind.Array && arr.RootElement.GetArrayLength() > 0) tmp = arr.RootElement[0].GetDouble(); } catch { } }
                            data.TempC = Math.Round(tmp, 1);
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                data.Error = ex.Message;
                data.AgentPing = false;
            }
            return data;
        }

        private static List<string> ParseDiscovery(string json)
        {
            var list = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                // Handle both {"data":[...]} and bare [...]
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
                    root = data;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in root.EnumerateArray())
                    {
                        if (el.TryGetProperty("{#FSNAME}", out var fs))
                            list.Add(fs.GetString() ?? "");
                    }
                }
            }
            catch { }
            if (list.Count == 0 && json.Contains("C:")) list.Add("C:");
            return list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> ParseNetIfDiscovery(string json)
        {
            var list = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
                    root = data;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in root.EnumerateArray())
                    {
                        if (el.TryGetProperty("{#IFNAME}", out var n))
                            list.Add(n.GetString() ?? "");
                    }
                }
            }
            catch { }
            return list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> ParseServiceDiscovery(string json)
        {
            var list = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
                    root = data;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in root.EnumerateArray())
                    {
                        if (el.TryGetProperty("{#SERVICE.NAME}", out var n))
                            list.Add(n.GetString() ?? "");
                    }
                }
            }
            catch { }
            return list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static async Task<bool> IsAgentAliveAsync(string host, int timeoutMs)
        {
            try
            {
                string r = await QueryRawAsync(host, "agent.ping", timeoutMs);
                return r.Trim() == "1";
            }
            catch { return false; }
        }
    }
}
