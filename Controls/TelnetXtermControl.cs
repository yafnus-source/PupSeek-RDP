using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ShadowRDP.Controls
{
    // v3.9.3 — Telnet-терминал: форк SshXtermControl с транспортом TcpClient.
    // Тот же xterm.js в WebView2 (полный ANSI/TUI), вместо SSH — голый TCP
    // с минимальным IAC-хендшейком. Логин/пароль вводятся интерактивно
    // в самом терминале (приглашения login:/Password: шлёт сервер).
    //
    // Политика IAC: соглашаемся только на ECHO/SGA со стороны сервера,
    // NAWS/TTYPE с нашей стороны (+ шлём размер окна); всё остальное —
    // вежливый отказ (WONT/DONT). Этого достаточно для сетевого железа,
    // принтеров, контроллеров и прочих telnet-хостов.
    public class TelnetXtermControl : UserControl
    {
        private WebView2? _webView;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private bool _ready;
        private int _cols = 80, _rows = 24;

        public string Host { get; set; } = "";
        public int Port { get; set; } = 23;

        public TelnetXtermControl()
        {
            BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            Load += async (_, _) => await InitializeAsync();
            Disposed += (_, _) => Cleanup();
        }

        private async Task InitializeAsync()
        {
            // xterm-файлы общие с SSH-контролом (распаковка один раз).
            var htmlPath = SshXtermControl.EnsureXtermFiles();

            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };
            Controls.Add(_webView);

            try
            {
                await _webView.EnsureCoreWebView2Async();
            }
            catch (Exception ex)
            {
                Logger.Warn($"TelnetXtermControl: WebView2 init failed: {ex.Message}");
                return;
            }

            _webView.CoreWebView2.WebMessageReceived += OnWebMessage;
            _webView.CoreWebView2.Navigate($"file:///{htmlPath.Replace('\\', '/')}");
        }

        public async Task ConnectAsync()
        {
            if (string.IsNullOrEmpty(Host))
                throw new InvalidOperationException("Host must be set before ConnectAsync()");

            for (var i = 0; i < 50 && !_ready; i++) await Task.Delay(100);
            if (!_ready)
            {
                Logger.Warn("TelnetXtermControl: terminal not ready after 5s — aborting connect");
                return;
            }

            try
            {
                _client = new TcpClient();
                // Таймаут подключения 8с — голый ConnectAsync висит до 20с+.
                var connectTask = _client.ConnectAsync(Host, Port);
                await connectTask.WaitAsync(TimeSpan.FromSeconds(8));
                _stream = _client.GetStream();
                _stream.ReadTimeout = 30000;
                _stream.WriteTimeout = 5000;
                _cts = new CancellationTokenSource();
                _ = ReadLoopAsync(_cts.Token);
                Logger.Info($"TelnetXtermControl: connected to {Host}:{Port}");
                ShadowRDP.Services.AuditLogger.Write("telnet.connect", $"{Host}:{Port}", "ok");
            }
            catch (Exception ex)
            {
                SendOutput($"\r\nTelnet error: {ex.Message}\r\n");
                Logger.Warn($"TelnetXtermControl: connect failed for {Host}:{Port}: {ex.Message}");
                ShadowRDP.Services.AuditLogger.Write("telnet.connect", $"{Host}:{Port}", "fail", ex.Message);
            }
        }

        // Telnet IAC-константы.
        private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251;
        private const byte SB = 250, SE = 240;
        private const byte OPT_ECHO = 1, OPT_SGA = 3, OPT_TTYPE = 24, OPT_NAWS = 31;

        private async Task ReadLoopAsync(CancellationToken token)
        {
            var buf = new byte[8192];
            // Хвост незавершённой IAC-последовательности (границы TCP-чанков).
            var pending = new System.Collections.Generic.List<byte>();
            try
            {
                while (!token.IsCancellationRequested && _stream != null)
                {
                    int read;
                    try
                    {
                        read = await _stream.ReadAsync(buf, 0, buf.Length, token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex)
                    {
                        Logger.Warn($"TelnetXtermControl: read loop: {ex.Message}");
                        break;
                    }
                    if (read == 0)
                    {
                        SendOutput("\r\n[connection closed]\r\n");
                        break;
                    }
                    var text = ProcessIncoming(buf, read, pending);
                    if (!string.IsNullOrEmpty(text)) SendOutput(text);
                }
            }
            catch (OperationCanceledException) { /* normal */ }
        }

        // Выкусывает IAC-команды из потока (с ответами), возвращает чистый текст.
        private string ProcessIncoming(byte[] buf, int len, System.Collections.Generic.List<byte> pending)
        {
            var data = new System.Collections.Generic.List<byte>(len + pending.Count);
            if (pending.Count > 0) { data.AddRange(pending); pending.Clear(); }
            for (int i = 0; i < len; i++) data.Add(buf[i]);

            var outBytes = new System.Collections.Generic.List<byte>(data.Count);
            int j = 0;
            while (j < data.Count)
            {
                if (data[j] != IAC) { outBytes.Add(data[j]); j++; continue; }
                // IAC: нужна минимум 1 команда следом.
                if (j + 1 >= data.Count) { pending.Add(IAC); break; }
                byte cmd = data[j + 1];
                if (cmd == IAC) { outBytes.Add(IAC); j += 2; continue; } // экранированный 255
                if (cmd == SB)
                {
                    // SB opt ... IAC SE — ищем конец; если оборвано — в pending.
                    int end = -1;
                    for (int k = j + 2; k + 1 < data.Count; k++)
                        if (data[k] == IAC && data[k + 1] == SE) { end = k; break; }
                    if (end < 0) { for (int k = j; k < data.Count; k++) pending.Add(data[k]); break; }
                    HandleSubneg(data, j + 2, end);
                    j = end + 2;
                    continue;
                }
                if (cmd == DO || cmd == DONT || cmd == WILL || cmd == WONT)
                {
                    if (j + 2 >= data.Count) { for (int k = j; k < data.Count; k++) pending.Add(data[k]); break; }
                    HandleNegotiation(cmd, data[j + 2]);
                    j += 3;
                    continue;
                }
                // Прочие (NOP, DM, BRK, AO, AYT...) — пропускаем.
                j += 2;
            }
            if (outBytes.Count == 0) return "";
            return Encoding.UTF8.GetString(outBytes.ToArray());
        }

        private void HandleNegotiation(byte cmd, byte opt)
        {
            try
            {
                if (_stream == null) return;
                // Сервер WILL ECHO/SGA → соглашаемся (DO).
                if (cmd == WILL && (opt == OPT_ECHO || opt == OPT_SGA))
                {
                    _stream.Write(new byte[] { IAC, DO, opt }, 0, 3);
                    return;
                }
                // Сервер DO NAWS/TTYPE → соглашаемся (WILL) + шлём параметры.
                if (cmd == DO && opt == OPT_NAWS)
                {
                    _stream.Write(new byte[] { IAC, WILL, opt }, 0, 3);
                    SendNaws();
                    return;
                }
                if (cmd == DO && opt == OPT_TTYPE)
                {
                    _stream.Write(new byte[] { IAC, WILL, opt }, 0, 3);
                    return;
                }
                // Всё остальное — вежливый отказ.
                if (cmd == DO) _stream.Write(new byte[] { IAC, WONT, opt }, 0, 3);
                else if (cmd == WILL) _stream.Write(new byte[] { IAC, DONT, opt }, 0, 3);
            }
            catch { }
        }

        private void HandleSubneg(System.Collections.Generic.List<byte> data, int from, int to)
        {
            try
            {
                // TTYPE SEND → отвечаем своим типом терминала.
                if (to - from >= 2 && data[from] == OPT_TTYPE && data[from + 1] == 1 && _stream != null)
                {
                    var name = Encoding.ASCII.GetBytes("xterm-256color");
                    var resp = new System.Collections.Generic.List<byte> { IAC, SB, OPT_TTYPE, 0 };
                    resp.AddRange(name);
                    resp.Add(IAC); resp.Add(SE);
                    var bytes = resp.ToArray();
                    _stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch { }
        }

        private void SendNaws()
        {
            try
            {
                if (_stream == null) return;
                byte[] msg =
                {
                    IAC, SB, OPT_NAWS,
                    (byte)((_cols >> 8) & 0xFF), (byte)(_cols & 0xFF),
                    (byte)((_rows >> 8) & 0xFF), (byte)(_rows & 0xFF),
                    IAC, SE
                };
                _stream.Write(msg, 0, msg.Length);
            }
            catch { }
        }

        // Push output to the JS terminal (как в SshXtermControl).
        private void SendOutput(string text)
        {
            if (_webView?.CoreWebView2 == null || !_ready) return;
            try
            {
                var data = System.Text.Json.JsonSerializer.Serialize(text);
                var envelope = $"{{\"type\":\"output\",\"data\":{data}}}";
                _webView.CoreWebView2.PostWebMessageAsJson(envelope);
            }
            catch (Exception ex)
            {
                Logger.Warn($"TelnetXtermControl: SendOutput failed: {ex.Message}");
            }
        }

        private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string? raw = null;
                try { raw = e.WebMessageAsJson; }
                catch { }
                if (string.IsNullOrEmpty(raw))
                {
                    try { raw = e.TryGetWebMessageAsString(); }
                    catch { return; }
                }
                if (string.IsNullOrEmpty(raw)) return;
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
                var type = typeProp.GetString();
                if (type == "ready")
                {
                    _ready = true;
                    Logger.Info("TelnetXtermControl: JS terminal ready");
                }
                else if (type == "input" && _stream != null && _stream.CanWrite)
                {
                    var data = doc.RootElement.GetProperty("data").GetString() ?? "";
                    // NVT: Enter шлём как CRLF (xterm отдаёт голый CR).
                    data = data.Replace("\r\n", "\n").Replace("\r", "\r\n").Replace("\n", "\r\n");
                    var bytes = Encoding.UTF8.GetBytes(data);
                    _stream.Write(bytes, 0, bytes.Length);
                }
                else if (type == "size")
                {
                    _cols = doc.RootElement.GetProperty("cols").GetInt32();
                    _rows = doc.RootElement.GetProperty("rows").GetInt32();
                    SendNaws();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"TelnetXtermControl: message parse failed: {ex.Message}");
            }
        }

        public void Cleanup()
        {
            try { _cts?.Cancel(); } catch { }
            try { _stream?.Close(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
            _cts = null;
        }
    }
}
