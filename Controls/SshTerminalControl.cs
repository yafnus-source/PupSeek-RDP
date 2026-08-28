using System;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Renci.SshNet;

namespace ShadowRDP.Controls
{
    // v2.1.92 — Embedded SSH terminal control. Renders remote shell output
    // in a RichTextBox + captures keystrokes and writes them to the SSH
    // ShellStream. Async read loop pulls bytes off the shell and appends
    // them to the output box.
    //
    // v3.2.1 — ANSI SGR (colour) sequences are now RENDERED via
    // RichTextBox.SelectionColor instead of stripped. So coloured prompts,
    // `ls --color`, `grep --color`, etc. now appear correctly. Cursor-move
    // / erase codes are still stripped (no full terminal emulator here).
    //
    // Passwords: prompted once at session activation, kept in memory only
    // for the lifetime of this control (no persistence to disk).
    public class SshTerminalControl : UserControl
    {
        private readonly RichTextBox _output;
        private SshClient? _client;
        private ShellStream? _shell;
        private CancellationTokenSource? _cts;
        // v3.2.1 — font 10 → 11 for legibility.
        private readonly Font _font = new Font("Consolas", 11F);

        // v3.2.1 — current SGR state for colour rendering.
        private Color _currentFg = Color.FromArgb(220, 220, 220);
        private bool _bold;

        // Standard ANSI 8-colour palette (30-37) and bright (90-97). Used
        // for the most common SGR sequences from `ls`, prompt themes, etc.
        private static readonly Color[] BasicColors =
        {
            Color.FromArgb(0,   0,   0),     // 30 black
            Color.FromArgb(205, 0,   0),     // 31 red
            Color.FromArgb(0,   205, 0),     // 32 green
            Color.FromArgb(205, 205, 0),     // 33 yellow
            Color.FromArgb(0,   0,   238),   // 34 blue
            Color.FromArgb(205, 0,   205),   // 35 magenta
            Color.FromArgb(0,   205, 205),   // 36 cyan
            Color.FromArgb(229, 229, 229)    // 37 white
        };
        private static readonly Color[] BrightColors =
        {
            Color.FromArgb(127, 127, 127),   // 90 bright black (gray)
            Color.FromArgb(255, 85,  85),    // 91 bright red
            Color.FromArgb(85,  255, 85),    // 92 bright green
            Color.FromArgb(255, 255, 85),    // 93 bright yellow
            Color.FromArgb(85,  85,  255),   // 94 bright blue
            Color.FromArgb(255, 85,  255),   // 95 bright magenta
            Color.FromArgb(85,  255, 255),   // 96 bright cyan
            Color.FromArgb(255, 255, 255)    // 97 bright white
        };

        // Connection params — set BEFORE calling ConnectAsync.
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";

        public SshTerminalControl()
        {
            _output = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = _font,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                WordWrap = false,
                Cursor = Cursors.IBeam
            };
            Controls.Add(_output);
            // Capture keys on the (read-only) RichTextBox. KeyDown fires
            // for non-character keys (Enter, Backspace, Tab, Arrows); KeyPress
            // for printable chars.
            _output.KeyDown += OnKeyDown;
            _output.KeyPress += OnKeyPress;
            Disposed += (_, _) => Cleanup();
        }

        public async Task ConnectAsync()
        {
            if (string.IsNullOrEmpty(Host) || string.IsNullOrEmpty(Username))
                throw new InvalidOperationException("Host and Username must be set before ConnectAsync()");

            AppendChunk($"Connecting to {Username}@{Host}:{Port}…\r\n");
            try
            {
                _client = new SshClient(Host, Port, Username, Password);
                await Task.Run(() => _client.Connect());
                // xterm-256color terminal name so most servers send colours.
                // 80×24 is the canonical fallback.
                _shell = _client.CreateShellStream("xterm-256color", 80, 24, 800, 600, 1024);
                _cts = new CancellationTokenSource();
                _ = ReadLoopAsync(_cts.Token);
                AppendChunk("Connected. Type commands below.\r\n\r\n");
                Logger.Info($"SSH: connected to {Username}@{Host}:{Port}");
            }
            catch (Exception ex)
            {
                AppendChunk($"ERROR: {ex.Message}\r\n");
                Logger.Warn($"SSH connect failed for {Username}@{Host}:{Port}: {ex.Message}");
                throw;
            }
        }

        // Background reader: pulls bytes from the shell stream and appends
        // to the RichTextBox. v3.2.1 — parses ANSI SGR sequences and renders
        // them via SelectionColor; strips cursor-move / erase codes.
        private async Task ReadLoopAsync(CancellationToken token)
        {
            var buf = new byte[4096];
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
                        Logger.Warn($"SSH read loop: {ex.Message}");
                        break;
                    }
                    if (read == 0)
                    {
                        // Shell closed (server disconnected).
                        Invoke(new Action(() =>
                        {
                            AppendChunk("\r\n[connection closed]\r\n");
                        }));
                        break;
                    }
                    // v3.2.1 — UTF-8 decode (was already UTF-8, but now
                    // explicit + passed through ANSI renderer).
                    var text = Encoding.UTF8.GetString(buf, 0, read);
                    Invoke(new Action(() =>
                    {
                        AppendChunk(text);
                        _output.ScrollToCaret();
                    }));
                }
            }
            catch (OperationCanceledException) { /* normal */ }
        }

        // Append a chunk of remote output to the RichTextBox, honouring ANSI
        // escape sequences. SGR (\x1b[...m) updates _currentFg; CSI cursor
        // moves and OSC are stripped. Must run on UI thread (touches
        // RichTextBox).
        private void AppendChunk(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var sb = new StringBuilder(text.Length);
            void Flush()
            {
                if (sb.Length == 0) return;
                _output.SelectionStart = _output.TextLength;
                _output.SelectionLength = 0;
                _output.SelectionColor = _bold
                    ? ControlPaint.Light(_currentFg, 0.1f)
                    : _currentFg;
                _output.AppendText(sb.ToString());
                sb.Clear();
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\x1b' && i + 1 < text.Length)
                {
                    char next = text[i + 1];
                    if (next == '[')
                    {
                        // CSI — flush pending text first so its colour is
                        // captured before we mutate _currentFg.
                        Flush();
                        int start = i + 2;
                        int end = start;
                        while (end < text.Length && (text[end] < 0x40 || text[end] > 0x7E)) end++;
                        if (end < text.Length)
                        {
                            char final = text[end];
                            string parms = text.Substring(start, end - start);
                            if (final == 'm') ApplySgr(parms);
                            else if (final == 'J')
                            {
                                // Erase display. 2J = clear whole screen.
                                if (parms == "" || parms == "2" || parms == "0") _output.Clear();
                            }
                            else if (final == 'K')
                            {
                                // Erase line — best-effort: append \r so the
                                // next write overwrites current line content.
                                // Not perfect but matches what most shells
                                // expect when redrawing a prompt line.
                            }
                            // Other CSI (cursor moves, save/restore cursor)
                            // — ignore; no full terminal emulator here.
                            i = end + 1;
                            continue;
                        }
                    }
                    else if (next == ']')
                    {
                        // OSC — skip until BEL (\x07) or ST (ESC \).
                        Flush();
                        i += 2;
                        while (i < text.Length && text[i] != '\x07')
                        {
                            if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\')
                            {
                                i += 2;
                                goto osc_done;
                            }
                            i++;
                        }
                        i++; // consume BEL
                        osc_done:;
                        continue;
                    }
                    else
                    {
                        // Other ESC sequences (single-char) — skip ESC + next.
                        Flush();
                        i += 2;
                        continue;
                    }
                }
                // Skip lone \r (RichTextBox handles \n as line break).
                if (c == '\r') { i++; continue; }
                sb.Append(c);
                i++;
            }
            Flush();
            // Reset selection colour so subsequent plain AppendText (status
            // messages etc.) uses the default ForeColor.
            _output.SelectionColor = _output.ForeColor;
        }

        // Apply an SGR parameter string (the part between \x1b[ and m).
        private void ApplySgr(string parms)
        {
            if (string.IsNullOrEmpty(parms)) parms = "0";
            foreach (var p in parms.Split(';'))
            {
                if (!int.TryParse(p, out int code)) continue;
                switch (code)
                {
                    case 0:  // reset
                        _currentFg = Color.FromArgb(220, 220, 220);
                        _bold = false;
                        break;
                    case 1:  // bold
                        _bold = true;
                        break;
                    case 2:  // dim
                        _bold = false;
                        break;
                    case 22:  // normal intensity
                        _bold = false;
                        break;
                    case 39:  // default fg
                        _currentFg = Color.FromArgb(220, 220, 220);
                        break;
                    default:
                        if (code >= 30 && code <= 37)
                            _currentFg = BasicColors[code - 30];
                        else if (code >= 90 && code <= 97)
                            _currentFg = BrightColors[code - 90];
                        // 40-47 / 100-107 (background) — not implemented,
                        // RichTextBox doesn't expose per-char background.
                        break;
                }
            }
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (_shell == null || !_shell.CanWrite) return;
            // Translate special keys to their terminal escape codes.
            switch (e.KeyCode)
            {
                case Keys.Enter:
                    _shell.Write("\r");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Back:
                    _shell.Write("\b");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Tab:
                    _shell.Write("\t");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Up:
                    _shell.Write("\x1b[A");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Down:
                    _shell.Write("\x1b[B");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Right:
                    _shell.Write("\x1b[C");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Left:
                    _shell.Write("\x1b[D");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Home:
                    _shell.Write("\x1b[H");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;
                case Keys.End:
                    _shell.Write("\x1b[F");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;
            }
            // Ctrl+C interrupts (ASCII ETX = 0x03).
            if (e.Control && e.KeyCode == Keys.C)
            {
                _shell.Write(new byte[] { 0x03 }, 0, 1);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            // Ctrl+D sends EOF (ASCII 0x04).
            if (e.Control && e.KeyCode == Keys.D)
            {
                _shell.Write(new byte[] { 0x04 }, 0, 1);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void OnKeyPress(object? sender, KeyPressEventArgs e)
        {
            if (_shell == null || !_shell.CanWrite) return;
            // Printable chars (incl. space) — forward as-is. Write expects
            // string, so convert the char (e.g. ssh_client.Write("a")).
            if (!char.IsControl(e.KeyChar))
            {
                _shell.Write(e.KeyChar.ToString());
                e.Handled = true;
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
