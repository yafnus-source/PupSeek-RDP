using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShadowRDP.Services
{
    // Quick "is this host alive" check with hybrid strategy: TCP connect to
    // RPC port 135 first (almost always open on Windows, even when ICMP is
    // firewalled), then ICMP ping as fallback.
    //
    // Why hybrid: on Win10/11 the default firewall profile blocks ICMP echo
    // requests, so a perfectly healthy PC looks "dead" via pure ping. RPC
    // port 135 is open by default on every domain-joined Windows machine
    // and is the same channel WMI uses — if 135 answers, WMI/QWinsta/etc.
    // will work too. ICMP stays as a fallback for the rare case where RPC
    // is blocked but ICMP is allowed.
    //
    // Total worst case: ~1.5s (800ms TCP + 700ms ICMP fallback). For 80 hosts
    // in parallel with 50 workers that's ~3s, vs. ~30s+ with the old
    // sync-over-async version that was starving the thread pool.
    public static class PingService
    {
        // RPC endpoint mapper — present on every Windows machine since NT 4.
        private const int ProbePort = 135;
        private const int TcpTimeoutMs = 800;
        private const int IcmpTimeoutMs = 700;

        public const int DefaultTimeoutMs = 1000;

        // Hybrid alive check, ASYNC. Try TCP 135 first (cancellation-aware
        // ConnectAsync), fall back to ICMP. This is the version all
        // parallel sweeps should use — the synchronous IsAlive() below is
        // kept only for callers that can't await.
        public static async Task<bool> IsAliveAsync(string computer, int timeoutMs = DefaultTimeoutMs)
        {
            if (string.IsNullOrWhiteSpace(computer)) return false;
            var name = computer.Trim();

            if (await IsPortOpenAsync(name, ProbePort, TcpTimeoutMs))
            {
                return true;
            }

            return await IcmpPingAsync(name, IcmpTimeoutMs);
        }

        // Sync wrapper. Uses Task.Run to avoid the sync-over-async thread
        // pool starvation that the previous Task.WhenAny(...).GetResult()
        // pattern caused (each blocked thread held a slot while the
        // connection attempt kept running in the background).
        //
        // Prefer IsAliveAsync in any code path that's already async.
        public static bool IsAlive(string computer, int timeoutMs = DefaultTimeoutMs)
        {
            return Task.Run(() => IsAliveAsync(computer, timeoutMs)).GetAwaiter().GetResult();
        }

        // Async TCP connect with hard timeout via CancellationToken. .NET 5+
        // ConnectAsync accepts a token — when it fires, the connection
        // attempt is genuinely cancelled (unlike Task.Delay racing, which
        // leaves the attempt running and the socket lingering).
        public static async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, cts.Token);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        // Async ICMP echo. Ping.SendPingAsync is the CancellationToken-less
        // variant — the timeoutMs param covers the cancellation.
        public static async Task<bool> IcmpPingAsync(string host, int timeoutMs)
        {
            try
            {
                using var pinger = new Ping();
                var reply = await pinger.SendPingAsync(host, timeoutMs);
                return reply != null && reply.Status == IPStatus.Success;
            }
            catch
            {
                return false;
            }
        }

        // Legacy sync TCP-connect-with-timeout. Kept for any caller that
        // still needs it; new code should use IsPortOpenAsync.
        public static bool IsPortOpen(string host, int port, int timeoutMs)
        {
            return Task.Run(() => IsPortOpenAsync(host, port, timeoutMs)).GetAwaiter().GetResult();
        }

        // Legacy sync ICMP ping.
        public static bool IcmpPing(string host, int timeoutMs)
        {
            try
            {
                using var pinger = new Ping();
                var reply = pinger.Send(host, timeoutMs);
                return reply != null && reply.Status == IPStatus.Success;
            }
            catch
            {
                return false;
            }
        }

        // For diagnostic UI: returns round-trip time in ms, or -1 on failure.
        public static int Roundtrip(string computer, int timeoutMs = DefaultTimeoutMs)
        {
            if (string.IsNullOrWhiteSpace(computer)) return -1;
            try
            {
                using var pinger = new Ping();
                var reply = pinger.Send(computer.Trim(), timeoutMs);
                if (reply != null && reply.Status == IPStatus.Success)
                {
                    return (int)reply.RoundtripTime;
                }
            }
            catch { }
            return -1;
        }

        // Open a cmd window that runs `ping -n 4` against the host. 4 pings
        // (~4 seconds on LAN), then cmd exits automatically — no need for the
        // user to close it. /c (vs /k) means "run command, then exit".
        //
        // This implements the user's request: "по кнопке ping вылезало окошко
        // cmd, где идет ping" — 4 раза, потом автоматический выход.
        public static void OpenInCmd(string computer)
        {
            if (string.IsNullOrWhiteSpace(computer))
            {
                throw new ArgumentException("Computer name required", nameof(computer));
            }
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping {computer.Trim()} -n 4",
                UseShellExecute = true,    // open in its own console window
                CreateNoWindow = false
            };
            try
            {
                Process.Start(psi);
                Logger.Info($"Opened cmd with ping -n 4 on {computer}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start ping cmd for {computer}: {ex.Message}");
                throw;
            }
        }
    }
}
