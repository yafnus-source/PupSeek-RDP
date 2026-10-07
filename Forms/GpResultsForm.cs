using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // Group Policy Results dialog. Runs `gpresult /S computer /SCOPE COMPUTER /R`
    // and renders the textual output in the same Excel-style table format as
    // ComputerInfoForm — sections get accent-blue header rows, key/value pairs
    // get banded data rows, list items nest under their parent.
    //
    // v4.3.3 — редизайн в стиле FTP-эталона (как окно информации о компьютере):
    // градиентный hero, таблица в карточке, кнопки в футере. Вся логика
    // (запуск gpresult, парсинг, кодировки, копирование) без изменений.
    //
    // Output encoding detection: gpresult writes 866 (OEM) on Russian Windows
    // when stdout is redirected. We read raw bytes and try UTF-8 → 866 → 1251
    // in order, scoring by Russian-letter density.
    public class GpResultsForm : Form
    {
        private readonly string _computer;
        private readonly DataGridView _grid;
        private readonly Label _refreshBtn;
        private readonly Label _copyBtn;
        private readonly Label _closeBtn;
        private readonly Label _statusLabel;

        public GpResultsForm(string computer)
        {
            _computer = computer;
            Text = $"Group Policy Results: {computer}";
            ClientSize = new Size(880, 726);
            MinimumSize = new Size(640, 546);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = UiTheme.DialogBackground;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; DialogResult = DialogResult.Cancel; Close(); } };

            // ——— 1. Hero (без крестика: у окна есть штатный) ———
            var hero = UiModal.Hero("Group Policy Results", computer, "gear");

            // ——— 2. Content: report grid inside a section card ———
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = UiTheme.DialogBackground, Padding = new Padding(14) };
            var card = UiModal.Card(0, 0, 100, 100, "", "", withHeader: false);
            card.Dock = DockStyle.Fill;
            content.Controls.Add(card);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { card.Region?.Dispose(); card.Region = null; } catch { }

            // === Grid ===
            _grid = new Controls.DoubleBufferedDataGridView
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false,
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Property", HeaderText = "Параметр", FillWeight = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Значение", FillWeight = 250 });
            ShadowRDP.Helpers.UiGridStyle.ApplyUnified(_grid, 36, 36);
            card.Controls.Add(_grid);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(_grid);
            RoundCorners(_grid, 7);
            card.Resize += (s, e) => { FitCardChild(card, _grid); try { card.Invalidate(true); } catch { } };
            _grid.Resize += (s, e) => { try { _grid.Invalidate(); } catch { } };
            _grid.ColumnWidthChanged += (s, e) => { try { _grid.Invalidate(); } catch { } };
            FitCardChild(card, _grid);

            // ——— 3. Status bar (thin strip above the footer) ———
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = UiTheme.DialogCard };
            statusPanel.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, statusPanel.Width, 0); };
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 0, 0),
                Text = "Запуск gpresult..."
            };
            statusPanel.Controls.Add(_statusLabel);

            // ——— 4. Footer with action buttons ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnFont = new Font("Segoe UI Semibold", 9F);
            int w(string text) => TextRenderer.MeasureText(text, btnFont).Width + 28;
            _refreshBtn = ActionButton("Обновить", w("Обновить"));
            _refreshBtn.Location = new Point(16, 15);
            _refreshBtn.Click += async (s, e) => await RunGpResultAsync();
            _copyBtn = ActionButton("Копировать в Excel", w("Копировать в Excel"));
            _copyBtn.Location = new Point(16 + _refreshBtn.Width + 8, 15);
            _copyBtn.Click += OnCopyClicked;
            _closeBtn = ActionButton("Закрыть", w("Закрыть"));
            _closeBtn.Click += (s, e) => Close();
            footer.Controls.AddRange(new Control[] { _refreshBtn, _copyBtn, _closeBtn });
            footer.Resize += (s, e) =>
            {
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
            };

            // Dock order: content Fill first, hero Top, then the two Bottom
            // strips (footer added last docks first → very bottom, status
            // above it). Same rule as the FTP sample modal.
            Controls.Add(content);
            Controls.Add(hero);
            Controls.Add(statusPanel);
            Controls.Add(footer);

            var dpiScale = UiModal.ApplyDpiScale(this);
            if (dpiScale > 1.001f)
                ClientSize = new Size((int)Math.Round(ClientSize.Width * dpiScale), (int)Math.Round(ClientSize.Height * dpiScale));

            Shown += async (s, e) =>
            {
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
                try { BeginInvoke(new Action(() => { try { Refresh(); } catch { } })); } catch { }
                await RunGpResultAsync();
            };
        }

        // Child of a headerless card: uniform 14px padding on all sides.
        private static void FitCardChild(Panel card, Control child)
        {
            child.Bounds = new Rectangle(14, 14,
                Math.Max(50, card.ClientSize.Width - 28),
                Math.Max(50, card.ClientSize.Height - 28));
        }

        // Скругление углов: режем Region контрола по скруглённому rect.
        private static void RoundCorners(Control c, int radius)
        {
            void Update()
            {
                if (c.IsDisposed || c.Width <= 0 || c.Height <= 0) return;
                c.Region?.Dispose();
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                var r = new Rectangle(0, 0, c.Width, c.Height);
                int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                c.Region = new Region(path);
            }
            c.Resize += (s, e) => Update();
            Update();
        }

        // Локальная filled-кнопка: тот же вид, что MakeCompactFilled, но текст
        // рисуется из lbl.Text — смена Text ("Скопировано!") реально видна.
        private static Label ActionButton(string text, int width, Color? bgOverride = null)
        {
            Color bg = bgOverride ?? UiTheme.HeaderBar;
            Color hover = ControlPaint.Dark(bg, 0.08f);
            var fill = new SolidBrush(bg);
            var lbl = new Label
            {
                Text = text,
                AutoSize = false,
                Size = new Size(width, 22),
                Font = new Font("Segoe UI Semibold", 9F),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.Transparent
            };
            lbl.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, lbl.Width, lbl.Height);
                using var path = UiButtons.RoundedRect(r, 1);
                if (lbl.Enabled) e.Graphics.FillPath(fill, path);
                else { using var dis = new SolidBrush(Color.FromArgb(0xE2, 0xE4, 0xE8)); e.Graphics.FillPath(dis, path); }
                var fg = lbl.Enabled ? Color.White : Color.FromArgb(0x8A, 0x8E, 0x94);
                TextRenderer.DrawText(e.Graphics, lbl.Text, lbl.Font, r, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            };
            lbl.MouseEnter += (s, e) => { if (lbl.Enabled) { fill.Color = hover; lbl.Invalidate(); } };
            lbl.MouseLeave += (s, e) => { if (lbl.Enabled) { fill.Color = bg; lbl.Invalidate(); } };
            lbl.EnabledChanged += (s, e) => lbl.Invalidate();
            return lbl;
        }

        private async Task RunGpResultAsync()
        {
            _grid.Rows.Clear();
            _grid.Rows.Add("Загрузка...", $"Выполняется gpresult.exe на {_computer} (может занять до 30 секунд)");
            _grid.Rows[0].Cells[1].Style.ForeColor = Color.Gray;
            _refreshBtn.Enabled = false;
            try
            {
                var text = await Task.Run(() => RunGpResultSync(_computer));
                var rows = GpResultParser.Parse(text);
                RenderRows(rows);
                _statusLabel.Text = $"Готово — {rows.Count} строк";
                Logger.Info($"GP Results on {_computer}: {text.Length} chars, {rows.Count} rows");
            }
            catch (Exception ex)
            {
                _grid.Rows.Clear();
                _grid.Rows.Add("Ошибка", ex.Message);
                _statusLabel.Text = $"Ошибка: {ex.Message}";
            }
            finally
            {
                _refreshBtn.Enabled = true;
            }
        }

        // Render parsed rows — sections get accent-blue header rows, data
        // rows get alternating white/light-grey bands. Same look as
        // ComputerInfoForm.
        private void RenderRows(List<(bool IsSection, string Property, string Value)> rows)
        {
            _grid.Rows.Clear();
            var sectionFont = new Font("Segoe UI Semibold", 9.5F);
            var sectionBg = ShadowRDP.Helpers.UiTheme.AccentSoft;
            var sectionFg = ShadowRDP.Helpers.UiTheme.Accent;
            var bandBg = ShadowRDP.Helpers.UiTheme.Background;
            var whiteBg = Color.White;
            var propColor = Color.FromArgb(90, 95, 105);
            var valueColor = ShadowRDP.Helpers.UiTheme.Foreground;
            int bandIdx = 0;

            foreach (var (isSection, prop, val) in rows)
            {
                if (isSection)
                {
                    bandIdx = 0;
                    var idx = _grid.Rows.Add(prop, "");
                    var r = _grid.Rows[idx];
                    r.Cells[0].Style.BackColor = sectionBg;
                    r.Cells[0].Style.ForeColor = sectionFg;
                    r.Cells[0].Style.Font = sectionFont;
                    r.Cells[1].Style.BackColor = sectionBg;
                    r.Cells[1].Style.ForeColor = sectionFg;
                    r.Cells[1].Style.Font = sectionFont;
                    r.Height = 26;
                }
                else
                {
                    var bg = (bandIdx % 2 == 1) ? bandBg : whiteBg;
                    var idx = _grid.Rows.Add(prop, val);
                    var r = _grid.Rows[idx];
                    r.Cells[0].Style.BackColor = bg;
                    r.Cells[0].Style.ForeColor = propColor;
                    r.Cells[1].Style.BackColor = bg;
                    r.Cells[1].Style.ForeColor = valueColor;
                    bandIdx++;
                }
            }
        }

        private void OnCopyClicked(object? sender, EventArgs e)
        {
            if (_grid.Rows.Count == 0) return;
            var sb = new StringBuilder();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var p = row.Cells[0].Value?.ToString() ?? "";
                var v = row.Cells[1].Value?.ToString() ?? "";
                if (!string.IsNullOrEmpty(p) && !string.IsNullOrEmpty(v)) sb.AppendLine($"{p}\t{v}");
                else if (!string.IsNullOrEmpty(p)) sb.AppendLine(p);
                else if (!string.IsNullOrEmpty(v)) sb.AppendLine($"\t{v}");
            }
            try { Clipboard.SetText(sb.ToString()); } catch { }
            _copyBtn.Text = "Скопировано!";
            var t = new Timer { Interval = 600 };
            t.Tick += (s, e) => { _copyBtn.Text = "Копировать в Excel"; t.Stop(); t.Dispose(); };
            t.Start();
        }

        // === gpresult.exe runner ===

        private static string RunGpResultSync(string computer)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gpresult.exe",
                Arguments = $"/S {computer} /SCOPE COMPUTER /R",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "Не удалось запустить gpresult.exe";
            var stdoutBytes = ReadAllBytes(proc.StandardOutput.BaseStream);
            var stderrBytes = ReadAllBytes(proc.StandardError.BaseStream);
            if (!proc.WaitForExit(60_000))
            {
                try { proc.Kill(); } catch { }
                return "gpresult превысил таймаут 60 секунд.";
            }

            var stdout = DecodeBestEffort(stdoutBytes);
            var stderr = DecodeBestEffort(stderrBytes);
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(stderr))
            {
                sb.AppendLine("[stderr]");
                sb.AppendLine(stderr);
                sb.AppendLine();
            }
            sb.AppendLine("[stdout]");
            sb.Append(stdout);
            if (proc.ExitCode != 0)
            {
                sb.AppendLine();
                sb.AppendLine($"[exit code: {proc.ExitCode}]");
                sb.AppendLine("Возможные причины: нет прав на цели, хост недоступен, RPC заблокирован.");
            }
            return sb.ToString();
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        // Try UTF-8 strict first. If it produces replacement chars, score
        // 866 vs 1251 by Russian-letter density.
        private static string DecodeBestEffort(byte[] bytes)
        {
            if (bytes.Length == 0) return "";
            var utf8 = Encoding.UTF8.GetString(bytes);
            if (!utf8.Contains('�')) return utf8;
            var cp866 = Encoding.GetEncoding(866).GetString(bytes);
            var cp1251 = Encoding.GetEncoding(1251).GetString(bytes);
            return ScoreRussian(cp866) >= ScoreRussian(cp1251) ? cp866 : cp1251;
        }

        private static int ScoreRussian(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int score = 0;
            foreach (var c in s) if (c >= 0x0410 && c <= 0x044F) score++;
            return score;
        }
    }

    // Parses gpresult's textual output into structured rows.
    //
    // gpresult /R output looks like:
    //   INFO: ... (or "ОШИБКА:" on Russian)
    //   === КОМПЬЮТЕР ===
    //   ПАРАМЕТРЫ КОМПЬЮТЕРА
    //   --------------------
    //       CN=...
    //       Last time Group Policy was applied: ...
    //       Group Policy was applied from: ...
    //
    //       Применявшиеся объекты группой политики
    //       --------------------------------------
    //           Default Domain Policy
    //           Another GPO
    //
    // We split on the "----" separator under section headers to identify
    // sub-sections, and treat lines as either key:value pairs or list items.
    public static class GpResultParser
    {
        public static List<(bool IsSection, string Property, string Value)> Parse(string text)
        {
            var rows = new List<(bool IsSection, string Property, string Value)>();
            if (string.IsNullOrWhiteSpace(text)) return rows;

            // Strip stderr/stdout markers we added ourselves for clean parse.
            text = text.Replace("[stderr]", "").Replace("[stdout]", "").Trim();

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            bool inList = false;
            string? pendingListTitle = null;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    // Blank line — end of list context.
                    inList = false;
                    pendingListTitle = null;
                    continue;
                }

                // Look ahead for "----" line — means this line is a sub-section
                // header (e.g. "Применявшиеся объекты группой политики").
                if (i + 1 < lines.Length && IsDashLine(lines[i + 1]))
                {
                    // Promote to section.
                    rows.Add((true, trimmed, ""));
                    inList = false;
                    pendingListTitle = trimmed;
                    i++; // skip the dashes
                    continue;
                }

                // KEY: VALUE pattern.
                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx > 0 && colonIdx < 40 && !trimmed.StartsWith("http"))
                {
                    var key = trimmed.Substring(0, colonIdx).Trim();
                    var val = trimmed.Substring(colonIdx + 1).Trim();
                    rows.Add((false, key, val));
                    inList = false;
                    continue;
                }

                // Standalone uppercase line without colon → section header.
                if (IsAllUpper(trimmed) && trimmed.Length > 3 && !trimmed.Contains(' '))
                {
                    rows.Add((true, trimmed, ""));
                    continue;
                }

                // Lines like "ПОЛИТИКА КОМПЬЮТЕРА" or "ПАРАМЕТРЫ КОМПЬЮТЕРА"
                // (multi-word uppercase headers).
                if (IsAllUpper(trimmed) && trimmed.Length > 3)
                {
                    rows.Add((true, trimmed, ""));
                    continue;
                }

                // Indented line under a list title → list item.
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                {
                    if (pendingListTitle != null)
                    {
                        rows.Add((false, "  •", trimmed));
                    }
                    else
                    {
                        rows.Add((false, "", trimmed));
                    }
                    inList = true;
                    continue;
                }

                // Default: treat as a value with empty key.
                rows.Add((false, "", trimmed));
            }

            return rows;
        }

        private static bool IsDashLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            var t = s.Trim();
            if (t.Length < 3) return false;
            foreach (var c in t) if (c != '-') return false;
            return true;
        }

        private static bool IsAllUpper(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            bool sawLetter = false;
            foreach (var c in s)
            {
                if (char.IsLetter(c))
                {
                    sawLetter = true;
                    if (char.IsLower(c)) return false;
                }
            }
            return sawLetter;
        }
    }
}
