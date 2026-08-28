using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Renci.SshNet;

namespace ShadowRDP.Controls
{
    // v3.2.3 — full terminal emulator for SSH via xterm.js inside WebView2.
    // Replaces the plain-RichTextBox SshTerminalControl which couldn't
    // render TUI apps (MC, vim, nano, less). xterm.js handles all ANSI
    // cursor moves, alternate screen, mouse events, etc.
    //
    // Resource layout: terminal.html + xterm.{js,css} + addon-fit.js are
    // embedded in the assembly (EmbeddedResource csproj entries) and
    // extracted to %LOCALAPPDATA%\PupSeekRDP\xterm\ on first use — WebView2
    // can't NavigateToString with relative script srcs, but file:// works
    // fine once the files are on disk.
    public class SshXtermControl : UserControl
    {
        private WebView2? _webView;
        private SshClient? _client;
        private ShellStream? _shell;
        private CancellationTokenSource? _cts;
        private bool _ready;
        private int _cols = 80, _rows = 24;

        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";

        public SshXtermControl()
        {
            BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            Load += async (_, _) => await InitializeAsync();
            Disposed += (_, _) => Cleanup();
        }

        private async Task InitializeAsync()
        {
            var htmlPath = EnsureXtermFiles();

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
                Logger.Warn($"SshXtermControl: WebView2 init failed: {ex.Message}");
                return;
            }

            _webView.CoreWebView2.WebMessageReceived += OnWebMessage;
            _webView.CoreWebView2.Navigate($"file:///{htmlPath.Replace('\\', '/')}");
        }

        // Extract xterm files from embedded resources to a stable disk folder.
        // Returns the full path to terminal.html. No-op on subsequent runs
        // unless the assembly version changed (then we re-extract).
        private static string EnsureXtermFiles()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PupSeekRDP", "xterm");
            Directory.CreateDirectory(dir);
            var asm = Assembly.GetExecutingAssembly();
            var versionMarker = Path.Combine(dir, ".version");
            var currentVersion = asm.GetName().Version?.ToString() ?? "0.0.0.0";

            try
            {
                if (!File.Exists(versionMarker) || File.ReadAllText(versionMarker) != currentVersion)
                {
                    foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
                    ExtractResource(asm, "xterm.terminal.html", Path.Combine(dir, "terminal.html"));
                    ExtractResource(asm, "xterm.xterm.js", Path.Combine(dir, "xterm.js"));
                    ExtractResource(asm, "xterm.xterm.css", Path.Combine(dir, "xterm.css"));
                    ExtractResource(asm, "xterm.addon-fit.js", Path.Combine(dir, "addon-fit.js"));
                    File.WriteAllText(versionMarker, currentVersion);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"SshXtermControl: extract failed: {ex.Message}");
            }
            return Path.Combine(dir, "terminal.html");
        }

        private static void ExtractResource(Assembly asm, string name, string dest)
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null)
            {
                Logger.Warn($"SshXtermControl: resource '{name}' not found");
                return;
            }
            using var fs = File.Create(dest);
            stream.CopyTo(fs);
        }

        // Called by the host form once SSH credentials are resolved.
        public async Task ConnectAsync()
        {
            if (string.IsNullOrEmpty(Host) || string.IsNullOrEmpty(Username))
                throw new InvalidOperationException("Host and Username must be set before ConnectAsync()");

            // Wait for WebView2 to signal "ready" — terminal is mounted and
            // listening for output. Bounded retry so we don't hang forever if
            // xterm.js fails to load.
            for (var i = 0; i < 50 && !_ready; i++) await Task.Delay(100);
            if (!_ready)
            {
                Logger.Warn("SshXtermControl: terminal not ready after 5s — aborting SSH connect");
                return;
            }

            try
            {
                _client = new SshClient(Host, Port, Username, Password);
                await Task.Run(() => _client.Connect());
                // cols/rows come from the JS side; we set a sane default
                // before the first resize message arrives.
                _shell = _client.CreateShellStream("xterm-256color", (uint)_cols, (uint)_rows, 800, 600, 4096);
                _cts = new CancellationTokenSource();
                _ = ReadLoopAsync(_cts.Token);
                Logger.Info($"SshXtermControl: connected to {Username}@{Host}:{Port}");
            }
            catch (Exception ex)
            {
                SendOutput($"\r\nSSH error: {ex.Message}\r\n");
                Logger.Warn($"SshXtermControl: connect failed for {Username}@{Host}:{Port}: {ex.Message}");
            }
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            var buf = new byte[8192];
            try
            {
                while (!token.IsCancellationRequested && _shell != null)
                {
                    int read;
                    try
                    {
                        read = await _shell.ReadAsync(buf, 0, buf.Length);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex)
                    {
                        Logger.Warn($"SshXtermControl: read loop: {ex.Message}");
                        break;
                    }
                    if (read == 0)
                    {
                        SendOutput("\r\n[connection closed]\r\n");
                        break;
                    }
                    // xterm.js wants a JS string — convert bytes via UTF-8.
                    // For binary-ish sequences (rare on a shell stream) this
                    // is fine, xterm.js handles them.
                    var text = Encoding.UTF8.GetString(buf, 0, read);
                    SendOutput(text);
                }
            }
            catch (OperationCanceledException) { /* normal */ }
        }

        // Push output to the JS terminal via PostWebMessageAsJson. Native API,
        // faster + more reliable than ExecuteScriptAsync.
        private void SendOutput(string text)
        {
            if (_webView?.CoreWebView2 == null || !_ready) return;
            try
            {
                // Build a JSON object message: {"type":"output","data":"..."}
                // Serialize the text properly (handles \n, \", unicode etc.),
                // then wrap in the envelope. PostWebMessageAsJson parses it
                // back into a JS object on the other side.
                var data = System.Text.Json.JsonSerializer.Serialize(text);
                var envelope = $"{{\"type\":\"output\",\"data\":{data}}}";
                _webView.CoreWebView2.PostWebMessageAsJson(envelope);
            }
            catch (Exception ex)
            {
                Logger.Warn($"SshXtermControl: SendOutput failed: {ex.Message}");
            }
        }

        private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                // v3.2.3 — TryGetWebMessageAsString threw for object
                // messages; WebMessageAsJson returns the JSON-encoded
                // payload reliably. We also support the "JSON-as-string"
                // shape in case TryGetWebMessageAsString was used.
                string? raw = null;
                try { raw = e.WebMessageAsJson; }
                catch { /* fall back to string */ }
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
                    Logger.Info("SshXtermControl: JS terminal ready");
                }
                else if (type == "input" && _shell != null && _shell.CanWrite)
                {
                    var data = doc.RootElement.GetProperty("data").GetString() ?? "";
                    _shell.Write(data);
                }
                else if (type == "size")
                {
                    _cols = doc.RootElement.GetProperty("cols").GetInt32();
                    _rows = doc.RootElement.GetProperty("rows").GetInt32();
                    Logger.Info($"SshXtermControl: size { _cols}x{_rows}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"SshXtermControl: message parse failed: {ex.Message}");
            }
        }

        public void Cleanup()
        {
            try { _cts?.Cancel(); } catch { }
            try { _shell?.Dispose(); } catch { }
            try { _client?.Disconnect(); } catch { }
            try { _client?.Dispose(); } catch { }
            _shell = null;
            _client = null;
            _cts = null;
        }
    }
}
