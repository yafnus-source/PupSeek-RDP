using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // Group Policy Results dialog. Runs `gpresult /S computer /SCOPE COMPUTER /R`
    // and renders the textual output in the same Excel-style table format as
    // ComputerInfoForm — sections get accent-blue header rows, key/value pairs
    // get banded data rows, list items nest under their parent.
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
            BackColor = ShadowRDP.Helpers.UiTheme.Background;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;

            // Header
            var headerPanel = Helpers.UiHeader.Create("Group Policy Results", _computer, "gear");
            headerPanel.Dock = DockStyle.Top;
            Controls.Add(headerPanel);

            // === Grid ===
            _grid = new DataGridView
            {
                Location = new Point(24, 82),
                Size = new Size(824, 510),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                BackgroundColor = ShadowRDP.Helpers.UiTheme.Card,
                BorderStyle = BorderStyle.FixedSingle,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = ShadowRDP.Helpers.UiTheme.Background,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                ColumnHeadersHeight = 30,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersDefaultCellStyle = {
                    BackColor = ShadowRDP.Helpers.UiTheme.Muted,
                    ForeColor = ShadowRDP.Helpers.UiTheme.MutedForeground,
                    Font = new Font("Segoe UI Semibold", 9F),
                    Alignment = DataGridViewContentAlignment.MiddleLeft
                },
                DefaultCellStyle = {
                    Font = new Font("Segoe UI", 9.5F),
                    SelectionBackColor = ShadowRDP.Helpers.UiTheme.AccentSoft,
                    SelectionForeColor = ShadowRDP.Helpers.UiTheme.Accent
                },
                RowTemplate = { Height = 24 }
            };
_grid.EnableHeadersVisualStyles = false;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Property", HeaderText = "Параметр", FillWeight = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Значение", FillWeight = 250 });
            ShadowRDP.Helpers.UiGridStyle.PaintHeaders(_grid);
            Controls.Add(_grid);

            // === Buttons ===
            _refreshBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Обновить");
            _refreshBtn.Location = new Point(24, 605);
            _refreshBtn.Click += async (s, e) => await RunGpResultAsync();
            Controls.Add(_refreshBtn);

            _copyBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Копировать в Excel");
            _copyBtn.Location = new Point(144, 605);
            _copyBtn.Click += OnCopyClicked;
            Controls.Add(_copyBtn);

            _closeBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Закрыть");
            _closeBtn.Location = new Point(738, 605);
            _closeBtn.Click += (s, e) => Close();
            Controls.Add(_closeBtn);
            // CancelButton requires IButtonControl; Label doesn't implement it.

            _statusLabel = new Label
            {
                Location = new Point(320, 610),
                Size = new Size(410, 22),
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Text = "Запуск gpresult..."
            };
            Controls.Add(_statusLabel);

            Shown += async (s, e) => await RunGpResultAsync();
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
            var rows = new List<(bool, string, string)>();
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
