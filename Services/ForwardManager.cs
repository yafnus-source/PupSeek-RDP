using System;
using System.Collections.Generic;
using Renci.SshNet;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // v3.9.4 — запущенные SSH-туннели (в памяти процесса). Запуск
    // блокирующий (Connect) — вызывать из Task.Run; состояние не
    // переживает перезапуск программы.
    public static class ForwardManager
    {
        private sealed class Running
        {
            public SshClient Client = null!;
            public ForwardedPort Port = null!;
            public string Label = "";
        }

        private static readonly Dictionary<string, Running> _running = new();
        private static readonly object _lock = new();

        // Форма подписывается для обновления статуса списка.
        public static event Action? StateChanged;
        private static void Raise() { try { StateChanged?.Invoke(); } catch { } }

        public static bool IsRunning(string id)
        {
            lock (_lock) return _running.ContainsKey(id);
        }

        public static List<string> RunningIds()
        {
            lock (_lock) return new List<string>(_running.Keys);
        }

        // "host" или "host:port" → (host, port).
        public static (string Host, int Port) ParseTarget(string target, int defaultPort = 22)
        {
            var s = (target ?? "").Trim();
            if (s.Length == 0) return ("", defaultPort);
            int colon = s.LastIndexOf(':');
            // IPv6-скобки не поддерживаем в Target (в книге — host[:port]).
            if (colon > 0 && int.TryParse(s.Substring(colon + 1), out var p) && p > 0 && p <= 65535)
                return (s.Substring(0, colon), p);
            return (s, defaultPort);
        }

        // Валидация записи перед запуском; null — ок.
        public static string? Validate(PortForward t)
        {
            if (t.BindPort < 1 || t.BindPort > 65535) return "Порт должен быть 1…65535.";
            if (t.Kind == "dynamic") return null;
            if (string.IsNullOrWhiteSpace(t.TargetHost)) return "Укажите хост назначения.";
            if (t.TargetPort < 1 || t.TargetPort > 65535) return "Порт назначения должен быть 1…65535.";
            return null;
        }

        // Блокирующее подключение + старт туннеля. Бросает исключение с
        // понятным текстом. marshal — форма для диалога trust host key.
        public static void Start(PortForward t, string host, int port,
            string username, string password, string? keyPath, System.Windows.Forms.Control? marshal)
        {
            if (IsRunning(t.Id))
                throw new InvalidOperationException("Туннель уже запущен.");
            var invalid = Validate(t);
            if (invalid != null) throw new InvalidOperationException(invalid);

            SshClient? client = null;
            ForwardedPort? fp = null;
            try
            {
                client = new SshClient(SshAuth.BuildConnectionInfo(host, port, username, password, keyPath));
                client.HostKeyReceived += (_, e) =>
                    Helpers.SshHostKeyTrust.Handle(e, host, port, marshal);
                client.Connect();

                fp = t.Kind switch
                {
                    "local" => new ForwardedPortLocal("127.0.0.1", (uint)t.BindPort,
                        t.TargetHost, (uint)t.TargetPort),
                    "remote" => new ForwardedPortRemote("127.0.0.1", (uint)t.BindPort,
                        t.TargetHost, (uint)t.TargetPort),
                    "dynamic" => new ForwardedPortDynamic("127.0.0.1", (uint)t.BindPort),
                    _ => throw new ArgumentException($"Неизвестный тип туннеля: {t.Kind}")
                };
                fp.Start();
                client.AddForwardedPort(fp);

                lock (_lock)
                {
                    if (_running.ContainsKey(t.Id)) throw new InvalidOperationException("Туннель уже запущен.");
                    _running[t.Id] = new Running
                    {
                        Client = client,
                        Port = fp,
                        Label = $"{t.Kind}:{t.BindPort}"
                    };
                }
                AuditLogger.Write("ssh.tunnel.start", $"{host}:{port} → {t.Kind}:{t.BindPort}",
                    "ok", t.Name);
            }
            catch (Exception ex)
            {
                try { fp?.Stop(); } catch { }
                try { client?.Disconnect(); } catch { }
                try { client?.Dispose(); } catch { }
                AuditLogger.Write("ssh.tunnel.start", $"{host}:{port} → {t.Kind}:{t.BindPort}",
                    "fail", ex.Message);
                throw;
            }
            finally
            {
                Raise();
            }
        }

        public static void Stop(string id)
        {
            Running? r;
            lock (_lock)
            {
                if (!_running.TryGetValue(id, out r)) return;
                _running.Remove(id);
            }
            try { r.Port.Stop(); } catch { }
            try { r.Client.RemoveForwardedPort(r.Port); } catch { }
            try { r.Client.Disconnect(); } catch { }
            try { r.Client.Dispose(); } catch { }
            AuditLogger.Write("ssh.tunnel.stop", r.Label, "ok");
            Raise();
        }

        public static void StopAll()
        {
            List<string> ids;
            lock (_lock) ids = new List<string>(_running.Keys);
            foreach (var id in ids) Stop(id);
        }
    }
}
