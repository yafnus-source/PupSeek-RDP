using System;
using System.IO;
using System.Linq;
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
        // v3.9.4 — панель сниппетов над терминалом.
        private readonly Panel _toolbar;
        private readonly Button _snipBtn;
        private readonly Button _snipMgrBtn;

        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        // v3.9.4 — путь к приватному ключу (ppk/pem/key). Задан и файл
        // существует → только publickey (Password = фраза ключа);
        // иначе → password auth. null — чистый пароль (как раньше).
        public string? PublicKeyPath { get; set; }

        public SshXtermControl()
        {
            BackColor = System.Drawing.Color.FromArgb(30, 30, 30);

            _toolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = System.Drawing.Color.FromArgb(38, 38, 38)
            };
            _snipBtn = DarkBtn("Сниппеты", 90);
            _snipBtn.Location = new System.Drawing.Point(4, 3);
            _snipBtn.Click += (_, _) => OpenSnippetPicker();
            _snipMgrBtn = DarkBtn("Управление…", 100);
            _snipMgrBtn.Location = new System.Drawing.Point(98, 3);
            _snipMgrBtn.Click += (_, _) => OpenSnippetManager();
            _toolbar.Controls.Add(_snipBtn);
            _toolbar.Controls.Add(_snipMgrBtn);
            Controls.Add(_toolbar);

            Load += async (_, _) => await InitializeAsync();
            Disposed += (_, _) => Cleanup();
        }

        private static Button DarkBtn(string text, int width) => new Button
        {
            Text = text,
            Size = new System.Drawing.Size(width, 22),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = System.Drawing.Color.FromArgb(70, 70, 70), BorderSize = 1 },
            BackColor = System.Drawing.Color.FromArgb(45, 45, 45),
            ForeColor = System.Drawing.Color.FromArgb(220, 220, 220),
            Font = new System.Drawing.Font("Segoe UI", 8.25F),
            Cursor = Cursors.Hand
        };

        private async Task InitializeAsync()
        {
            var htmlPath = EnsureXtermFiles();

            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };
            Controls.Add(_webView);
            // Тулбар — поверх терминала: без этого Fill, добавленный позже,
            // ложится поверх и кнопки загораживают верх текста (z-order).
            try { _toolbar.BringToFront(); } catch { }

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
        // v3.9.3 — internal: xterm-файлы общие с TelnetXtermControl.
        internal static string EnsureXtermFiles()
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

        internal static void ExtractResource(Assembly asm, string name, string dest)
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
                await Task.Run(OpenConnection);
                // cols/rows come from the JS side; we set a sane default
                // before the first resize message arrives.
                _cts = new CancellationTokenSource();
                _ = ReadLoopAsync(_cts.Token);
                Logger.Info($"SshXtermControl: connected to {Username}@{Host}:{Port}");
                // v3.9.3 — аудит подключений (пароль — никогда).
                ShadowRDP.Services.AuditLogger.Write("ssh.connect", $"{Username}@{Host}:{Port}", "ok");
            }
            catch (Exception ex)
            {
                SendOutput($"\r\nSSH error: {ex.Message}\r\n");
                Logger.Warn($"SshXtermControl: connect failed for {Username}@{Host}:{Port}: {ex.Message}");
            }
        }

        private bool _keyWarned;

        // v3.9.4 — открытие подключения (клиент + шелл). Бросает при
        // неудаче; поля подменяются только после полного успеха.
        private void OpenConnection()
        {
            if (!_keyWarned && Services.SshAuth.KeyConfigured(PublicKeyPath) && !Services.SshAuth.KeyUsable(PublicKeyPath))
            {
                _keyWarned = true;
                SendOutput($"[warn] ключ '{PublicKeyPath}' не найден — подключение по паролю\r\n");
            }
            var client = new SshClient(Services.SshAuth.BuildConnectionInfo(
                Host, Port, Username, Password, PublicKeyPath));
            client.HostKeyReceived += (_, e) =>
                Helpers.SshHostKeyTrust.Handle(e, Host, Port, this);
            client.Connect();
            // cols/rows come from the JS side; we set a sane default
            // before the first resize message arrives.
            var shell = client.CreateShellStream("xterm-256color", (uint)_cols, (uint)_rows, 800, 600, 4096);
            _client = client;
            _shell = shell;
        }

        // v3.9.4 — закрыть транспорт, НУЛЬ-ссылки не трогаем: в цикле
        // чтения старый disposed шелл даст исключение → счётчик обрывов.
        private void CloseTransport()
        {
            try { _shell?.Dispose(); } catch { }
            try { _client?.Disconnect(); } catch { }
            try { _client?.Dispose(); } catch { }
        }

        // v3.9.4 — авто-переподключение при обрыве: 3 попытки, задержки
        // 2/5/15с. true — читать дальше; false — отмена/Cleanup.
        private async Task<bool> ReconnectAsync(int attempt, CancellationToken token)
        {
            int delay = attempt == 1 ? 2 : attempt == 2 ? 5 : 15;
            SendOutput($"\r\n[соединение потеряно — переподключение через {delay}с (попытка {attempt}/3)]\r\n");
            try { await Task.Delay(TimeSpan.FromSeconds(delay), token); }
            catch { return false; }
            CloseTransport();
            try
            {
                await Task.Run(OpenConnection, token);
                SendOutput("[переподключено]\r\n");
                Logger.Info($"SshXtermControl: reconnected to {Username}@{Host}:{Port} (attempt {attempt})");
                ShadowRDP.Services.AuditLogger.Write("ssh.reconnect",
                    $"{Username}@{Host}:{Port}", "ok", $"attempt={attempt}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"SshXtermControl: reconnect attempt {attempt} failed: {ex.Message}");
                ShadowRDP.Services.AuditLogger.Write("ssh.reconnect",
                    $"{Username}@{Host}:{Port}", "fail", $"attempt={attempt}: {ex.Message}");
                // Вернуться в цикл: старый шелл disposed → новая попытка.
                return true;
            }
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            var buf = new byte[8192];
            int failures = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var shell = _shell;
                    if (shell == null) { await Task.Delay(500, token); continue; }
                    int read;
                    try
                    {
                        read = await shell.ReadAsync(buf, 0, buf.Length);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { read = -1; }
                    catch (Exception ex)
                    {
                        Logger.Warn($"SshXtermControl: read loop: {ex.Message}");
                        read = -1;
                    }
                    if (read > 0)
                    {
                        failures = 0;
                        // xterm.js wants a JS string — convert bytes via UTF-8.
                        SendOutput(Encoding.UTF8.GetString(buf, 0, read));
                        continue;
                    }
                    if (token.IsCancellationRequested) break;
                    // Обрыв → авто-переподключение (2/5/15с, максимум 3).
                    if (failures >= 3)
                    {
                        SendOutput("\r\n[connection closed]\r\n");
                        break;
                    }
                    failures++;
                    if (!await ReconnectAsync(failures, token)) break;
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

        // === v3.9.4 — сниппеты SSH ===

        // Отправить строку в открытый шелл (сниппеты, программный ввод).
        // false — нет подключения/канал не пишется.
        public bool SendCommand(string command, bool enter = true)
        {
            var shell = _shell;
            if (shell == null || !shell.CanWrite) return false;
            try
            {
                shell.Write(enter ? command + "\r" : command);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"SshXtermControl: SendCommand failed: {ex.Message}");
                return false;
            }
        }

        private void OpenSnippetManager()
        {
            try { Forms.SnippetManagerForm.Show(this); }
            catch (Exception ex) { Logger.Warn($"Сниппеты: не удалось открыть менеджер: {ex.Message}"); }
        }

        private void RunSnippet(Models.SshSnippet s)
        {
            if (!SendCommand(s.Command))
            {
                MessageBox.Show(this, "Нет активного подключения к серверу.", "Сниппеты",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Services.AuditLogger.Write("ssh.snippet", $"{Username}@{Host}:{Port}", "sent", s.Name);
        }

        // v3.9.5 — «Сниппеты» открывает окно-пикер (список + запуск),
        // «Управление…» — редактор; раньше обе кнопки вели к одному окну
        // (меню называлось так же, как менеджер, и при пустом сторе
        // содержало единственный пункт «Управление сниппетами…»).
        private void OpenSnippetPicker()
        {
            try { Forms.SshSnippetPickerForm.Show(this, RunSnippet); }
            catch (Exception ex) { Logger.Warn($"Сниппеты: пикер не открылся: {ex.Message}"); }
        }
    }
}
