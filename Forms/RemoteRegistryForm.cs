using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using ShadowRDP.Controls;

namespace ShadowRDP.Forms
{
    // "Удаленный реестр" — браузер + редактор реестра удалённого компьютера.
    // Открывается из ДОМЕН-подменю (книга, сессии): regedit.exe не умеет
    // подключаться к удалённой машине через параметры запуска, поэтому смотрим
    // и правим реестр сами через RegistryKey.OpenRemoteBaseKey (кусты HKLM
    // и HKU, подразделы подгружаются лениво при раскрытии узла).
    //
    // Поиск — бокс в hero, как фильтр в Открытых файлах: фоновое сканирование
    // обоих кустов (имя раздела/параметра + строковые данные), результаты
    // гридом, даблклик ведёт к разделу в дереве.
    //
    // Редактирование — как в regedit: создать/изменить/переименовать/удалить
    // разделы и параметры всех типов, с подтверждениями на удаление.
    //
    // На цели должна быть запущена служба "Удаленный реестр" (RemoteRegistry) —
    // если подключения нет, предлагаем запустить её через sc.exe и повторяем
    // попытку.
    public class RemoteRegistryForm : Form
    {
        private readonly string _computer;
        private readonly ScrollHiddenTreeView _tree;
        private readonly ScrollHiddenListView _values;
        private readonly Label _statusLabel;
        private readonly Label _refreshBtn;
        private readonly Label _closeBtn;
        // Поиск.
        private readonly TextBox _searchBox;
        private readonly Label _findBtn;
        private readonly DataGridView _results;
        private readonly SplitContainer _split;
        private CancellationTokenSource? _searchCts;
        private bool _searching;
        private string _searchTerm = "";
        private int _searchFound;
        private int _searchScanned;
        private const int SearchMaxResults = 5000;

        private sealed class NodeTag
        {
            public RegistryHive Hive;
            public string Path = "";
        }
        private static readonly object DummyTag = new object();

        public RemoteRegistryForm(string computer)
        {
            _computer = (computer ?? "").Trim().TrimStart('\\');
            Text = $"Удаленный реестр — {_computer}";
            ClientSize = new Size(920, 646);
            MinimumSize = new Size(680, 506);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = ShadowRDP.Helpers.UiTheme.DialogBackground;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; DialogResult = DialogResult.Cancel; Close(); } };

            // ——— 1. Hero + поиск справа (как фильтр в Открытых файлах) ———
            var hero = Helpers.UiModal.Hero("Удаленный реестр", _computer, "key");
            var searchWrapper = Helpers.UiSearchBox.Create("Поиск в реестре…", _ => { }, width: 300, height: 27, background: Color.White);
            _searchBox = Helpers.UiSearchBox.GetBox(searchWrapper);
            _searchBox.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ToggleSearchAsync(); }
            };
            var searchHint = new Label { Text = "(имя раздела/параметра или значение, Enter — найти)", Location = new Point(70, 48), Size = new Size(300, 18), Font = new Font("Segoe UI", 8F), ForeColor = Color.FromArgb(210, 228, 238), TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
            hero.Controls.Add(searchWrapper);
            hero.Controls.Add(searchHint);

            // ——— 2. Content: одна карточка со сплитом дерево/значения ———
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = ShadowRDP.Helpers.UiTheme.DialogBackground };
            var card = Helpers.UiModal.Card(0, 0, 100, 100, "", "", withHeader: false);
            content.Controls.Add(card);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            try { card.Region?.Dispose(); card.Region = null; } catch { }

            _split = new SplitContainer
            {
                BorderStyle = BorderStyle.None,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.None
            };
            _tree = new ScrollHiddenTreeView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                Font = new Font("Segoe UI", 9F),
                HideSelection = false,
                ShowLines = true
            };
            _tree.BeforeExpand += OnTreeBeforeExpand;
            _tree.AfterSelect += OnTreeAfterSelect;
            _tree.MouseUp += OnTreeMouseUp;
            _split.Panel1.Controls.Add(_tree);
            ShadowScroll.AttachTreeView(_tree);
            _values = new ScrollHiddenListView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                Font = new Font("Segoe UI", 9F),
                View = View.Details,
                FullRowSelect = true,
                GridLines = false
            };
            _values.Columns.Add("Имя", 220);
            _values.Columns.Add("Тип", 130);
            _values.Columns.Add("Данные", 400);
            _values.MouseUp += OnValuesMouseUp;
            _values.DoubleClick += (s, e) => { _ = EditSelectedValueAsync(); };
            _values.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) { e.Handled = true; _ = DeleteSelectedValueAsync(); }
            };
            _split.Panel2.Controls.Add(_values);
            ShadowScroll.AttachListView(_values);
            card.Controls.Add(_split);
            RoundCorners(_split, 7);
            card.Resize += (s, e) => { FitCardChild(card, _split); FitCardChild(card, _results); try { card.Invalidate(true); } catch { } };

            // Результаты поиска — грид поверх той же карточки (сплит прячем).
            _results = new DoubleBufferedDataGridView
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false,
                Visible = false
            };
            _results.Columns.Add(new DataGridViewTextBoxColumn { Name = "Location", HeaderText = "Раздел", FillWeight = 320, MinimumWidth = 200 });
            _results.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Имя", FillWeight = 150, MinimumWidth = 100 });
            _results.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Тип", FillWeight = 90, MinimumWidth = 80 });
            _results.Columns.Add(new DataGridViewTextBoxColumn { Name = "Data", HeaderText = "Данные", FillWeight = 200, MinimumWidth = 120 });
            ShadowRDP.Helpers.UiGridStyle.ApplyUnified(_results, 30, 30);
            _results.DefaultCellStyle.BackColor = Color.FromArgb(0xFD, 0xFB, 0xF7);
            _results.CellDoubleClick += async (s, e) => { if (e.RowIndex >= 0) await NavigateToResultAsync(_results.Rows[e.RowIndex]); };
            card.Controls.Add(_results);
            RoundCorners(_results, 7);
            ShadowScroll.AttachGrid(_results);

            // ——— 3. Status bar (thin strip above the footer) ———
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = ShadowRDP.Helpers.UiTheme.DialogCard };
            statusPanel.Paint += (s, e) => { using var pen = new Pen(ShadowRDP.Helpers.UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, statusPanel.Width, 0); };
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 0, 0),
                Text = "Подключение..."
            };
            statusPanel.Controls.Add(_statusLabel);

            // ——— 4. Footer with action buttons ———
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = ShadowRDP.Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(ShadowRDP.Helpers.UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnFont = new Font("Segoe UI Semibold", 9F);
            int w(string text) => TextRenderer.MeasureText(text, btnFont).Width + 28;
            _findBtn = Helpers.UiButtons.MakeCompactFilled("Найти", w("Найти"));
            _findBtn.Location = new Point(16, 15);
            _findBtn.Enabled = false;
            _findBtn.Click += async (s, e) => await ToggleSearchAsync();
            _refreshBtn = Helpers.UiButtons.MakeCompactFilled("Обновить", w("Обновить"));
            _refreshBtn.Location = new Point(16 + _findBtn.Width + 8, 15);
            _refreshBtn.Enabled = false;
            _refreshBtn.Click += async (s, e) => await RefreshSelectedAsync();
            _closeBtn = Helpers.UiButtons.MakeCompactFilled("Закрыть", w("Закрыть"));
            _closeBtn.Click += (s, e) => Close();
            footer.Controls.AddRange(new Control[] { _findBtn, _refreshBtn, _closeBtn });
            footer.Resize += (s, e) =>
            {
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
            };

            Controls.Add(content);
            Controls.Add(hero);
            Controls.Add(statusPanel);
            Controls.Add(footer);

            var dpiScale = Helpers.UiModal.ApplyDpiScale(this);
            if (dpiScale > 1.001f)
                ClientSize = new Size((int)Math.Round(ClientSize.Width * dpiScale), (int)Math.Round(ClientSize.Height * dpiScale));

            ShadowRDP.Services.WindowStateStore.Register(this, "remoteRegistry");
            FormClosing += (s, e) => ShadowRDP.Services.WindowStateStore.Save(this, "remoteRegistry");

            content.Resize += (s, e) => LayoutContent();
            hero.Resize += (s, e) => PositionSearch();
            Shown += async (s, e) =>
            {
                LayoutContent();
                PositionSearch();
                _closeBtn.Left = footer.ClientSize.Width - 16 - _closeBtn.Width;
                _closeBtn.Top = 15;
                try { _split.SplitterDistance = Math.Max(200, _split.Width * 45 / 100); } catch { }
                try { BeginInvoke(new Action(() => { try { Refresh(); } catch { } })); } catch { }
                await ConnectAsync();
            };

            void LayoutContent()
            {
                int W = content.ClientSize.Width, H = content.ClientSize.Height;
                card.Bounds = new Rectangle(14, 14, Math.Max(100, W - 28), Math.Max(100, H - 28));
            }
            LayoutContent();

            void PositionSearch()
            {
                int x = Math.Max(16, hero.Width - 316);
                searchWrapper.Location = new Point(x, 18);
                searchHint.Location = new Point(x, 48);
            }
            PositionSearch();
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

        // === Подключение ===
        private async Task ConnectAsync()
        {
            SetStatus($"Подключение к {_computer}...");
            var ok = await Task.Run(() => TryProbe());
            if (IsDisposed) return;
            if (ok)
            {
                AddHiveNode("HKEY_LOCAL_MACHINE", RegistryHive.LocalMachine);
                AddHiveNode("HKEY_USERS", RegistryHive.Users);
                if (IsDisposed) return;
                _refreshBtn.Enabled = true;
                _findBtn.Enabled = true;
                SetStatus($"Подключено к {_computer}. Раскройте куст слева.");
                Logger.Info($"RemoteRegistry: connected to {_computer}");
                return;
            }
            // Частая причина — остановлена служба RemoteRegistry (по умолчанию
            // отключена на современных Windows). Предлагаем запустить.
            var r = MessageBox.Show(this,
                $"Не удалось подключиться к реестру {_computer}.\n\n" +
                "Обычно это значит, что на нём остановлена служба\n" +
                "«Удаленный реестр» (RemoteRegistry).\n\n" +
                "Попробовать запустить её сейчас (нужны права админа на цели)?",
                "Удаленный реестр", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes)
            {
                SetStatus($"Нет подключения к {_computer}. Нужна служба RemoteRegistry на цели.");
                return;
            }
            SetStatus($"Запуск службы RemoteRegistry на {_computer}...");
            ok = await EnsureServiceRunningAsync();
            if (IsDisposed) return;
            if (ok)
            {
                AddHiveNode("HKEY_LOCAL_MACHINE", RegistryHive.LocalMachine);
                AddHiveNode("HKEY_USERS", RegistryHive.Users);
                if (IsDisposed) return;
                _refreshBtn.Enabled = true;
                _findBtn.Enabled = true;
                SetStatus($"Подключено к {_computer}. Раскройте куст слева.");
                Logger.Info($"RemoteRegistry: connected to {_computer} after service start");
            }
            else
            {
                SetStatus($"Нет подключения к {_computer}. Проверьте сеть, права и службу RemoteRegistry.");
                if (IsDisposed) return;
                MessageBox.Show(this,
                    $"Подключиться не вышло.\n\nПроверьте:\n" +
                    "• доступность компьютера по сети;\n" +
                    "• права администратора на нём;\n" +
                    $"• службу RemoteRegistry (sc \\\\{_computer} query RemoteRegistry).\n\n" +
                    $"Ответ sc.exe: {_lastScOutput}",
                    "Удаленный реестр", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool TryProbe()
        {
            try
            {
                using var root = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, _computer);
                // Любое чтение подтверждает, что RPC/реестр отвечают.
                _ = root.SubKeyCount;
                return true;
            }
            catch { return false; }
        }

        // Служба часто не просто остановлена, а ОТКЛЮЧЕНА (start= disabled) —
        // тогда голый `sc start` падает с 1058. Поэтому сначала переводим её
        // в demand (вручную), потом стартуем, затем опрашиваем реестр, пока
        // не ответит (до ~14 сек). Вывод sc сохраняем — показываем в ошибке.
        private string _lastScOutput = "";

        private async Task<bool> EnsureServiceRunningAsync()
        {
            await Task.Run(() => RunSc("config RemoteRegistry start= demand"));
            await Task.Run(() => RunSc("start RemoteRegistry"));
            for (int i = 0; i < 6; i++)
            {
                if (await Task.Run(() => TryProbe())) return true;
                await Task.Delay(2000);
            }
            return await Task.Run(() => TryProbe());
        }

        private string RunSc(string args)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"\\\\{_computer} {args}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (proc == null) return "(не запустился sc.exe)";
                var so = proc.StandardOutput.ReadToEnd();
                var se = proc.StandardError.ReadToEnd();
                proc.WaitForExit(15000);
                var full = (so + " " + se).Trim();
                _lastScOutput = full.Length > 300 ? full.Substring(0, 300) + "…" : full;
                Logger.Info($"RemoteRegistry sc {args} on {_computer}: {_lastScOutput}");
                return _lastScOutput;
            }
            catch (Exception ex)
            {
                _lastScOutput = ex.Message;
                Logger.Warn($"RemoteRegistry sc {args} failed: {ex.Message}");
                return _lastScOutput;
            }
        }

        private void AddHiveNode(string name, RegistryHive hive)
        {
            if (IsDisposed) return;
            var node = new TreeNode(name) { Tag = new NodeTag { Hive = hive, Path = "" } };
            node.Nodes.Add(new TreeNode("…") { Tag = DummyTag });
            _tree.Nodes.Add(node);
        }

        // === Ленивая подгрузка подразделов ===
        private (string[] subs, string? error) TryLoadSubKeys(NodeTag tag)
        {
            try
            {
                using var root = RegistryKey.OpenRemoteBaseKey(tag.Hive, _computer);
                using var key = string.IsNullOrEmpty(tag.Path) ? root : root.OpenSubKey(tag.Path, false);
                var subs = key?.GetSubKeyNames() ?? Array.Empty<string>();
                Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
                return (subs, null);
            }
            catch (Exception ex)
            {
                return (Array.Empty<string>(), ShortError(ex));
            }
        }

        private static void FillNode(TreeNode node, NodeTag tag, string[] subs)
        {
            node.Nodes.Clear();
            foreach (var s in subs)
            {
                var child = new TreeNode(s)
                {
                    Tag = new NodeTag { Hive = tag.Hive, Path = string.IsNullOrEmpty(tag.Path) ? s : tag.Path + "\\" + s }
                };
                child.Nodes.Add(new TreeNode("…") { Tag = DummyTag });
                node.Nodes.Add(child);
            }
        }

        private void OnTreeBeforeExpand(object? sender, TreeViewCancelEventArgs e)
        {
            if (e.Node == null) return;
            if (e.Node.Nodes.Count != 1 || !ReferenceEquals(e.Node.Nodes[0].Tag, DummyTag)) return;
            var tag = e.Node.Tag as NodeTag;
            if (tag == null) return;
            var node = e.Node;
            Task.Run(() =>
            {
                var (subs, error) = TryLoadSubKeys(tag);
                BeginInvokeSafe(() =>
                {
                    if (error != null)
                    {
                        node.Nodes.Clear();
                        node.Nodes.Add(new TreeNode($"(нет доступа: {error})") { Tag = DummyTag });
                        return;
                    }
                    FillNode(node, tag, subs);
                    if (subs.Length == 0) SetStatus($"{FullPath(tag)} — подразделов нет.");
                });
            });
        }

        private void OnTreeAfterSelect(object? sender, TreeViewEventArgs e)
        {
            if (e.Node == null) return;
            var tag = e.Node.Tag as NodeTag;
            if (tag == null) return;
            _ = LoadValuesAsync(tag);
        }

        private async Task RefreshSelectedAsync()
        {
            var node = _tree.SelectedNode;
            var tag = node?.Tag as NodeTag;
            if (tag == null) return;
            // Перечитываем подразделы узла + значения.
            node.Nodes.Clear();
            node.Nodes.Add(new TreeNode("…") { Tag = DummyTag });
            node.Collapse();
            await LoadValuesAsync(tag);
            node.Expand();
        }

        // === Поиск по реестру ===
        private sealed class SearchHit
        {
            public RegistryHive Hive;
            public string Path = "";
            public string? ValueName; // null = совпал сам раздел
            public string Kind = "";
            public string Display = "";
            public string Data = "";
        }

        private sealed class ResultTag
        {
            public RegistryHive Hive;
            public string Path = "";
            public string? ValueName;
        }

        private void EnterSearchMode()
        {
            _split.Visible = false;
            _results.Visible = true;
            _results.Rows.Clear();
            var card = _results.Parent as Panel;
            if (card != null) FitCardChild(card, _results);
        }

        private void ExitSearchMode()
        {
            try { _searchCts?.Cancel(); } catch { }
            _results.Visible = false;
            _split.Visible = true;
        }

        private async Task ToggleSearchAsync()
        {
            if (_searching) { try { _searchCts?.Cancel(); } catch { } return; }
            if (_tree.Nodes.Count == 0)
            {
                MessageBox.Show(this, "Сначала подключитесь к реестру.", "Поиск",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var term = _searchBox.Text.Trim();
            if (term.Length < 2)
            {
                MessageBox.Show(this, "Введите минимум 2 символа.", "Поиск",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            EnterSearchMode();
            _searching = true;
            _searchTerm = term;
            _searchFound = 0;
            _searchScanned = 0;
            try { _findBtn.Text = "Стоп"; } catch { }
            _refreshBtn.Enabled = false;
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;
            SetStatus($"Поиск «{term}»...");
            Logger.Info($"RemoteRegistry search on {_computer}: '{term}'");
            try
            {
                var hives = new[] { RegistryHive.LocalMachine, RegistryHive.Users };
                foreach (var hive in hives)
                    await Task.Run(() => ScanKey(hive, "", term, ct), ct);
                int f = Volatile.Read(ref _searchFound);
                SetStatus($"Поиск «{term}» завершён: просканировано {_searchScanned}, найдено {f}" +
                    (f >= SearchMaxResults ? " (показаны первые 5000)." : "."));
            }
            catch (OperationCanceledException)
            {
                SetStatus($"Поиск прерван: просканировано {_searchScanned}, найдено {_searchFound}.");
            }
            catch (Exception ex)
            {
                SetStatus($"Поиск: ошибка {ShortError(ex)}");
            }
            finally
            {
                _searching = false;
                try { _findBtn.Text = "Найти"; } catch { }
                _refreshBtn.Enabled = true;
                try { _searchCts?.Dispose(); } catch { }
                _searchCts = null;
            }
        }

        // Обход поддерева: каждый вызов открывает свой хэндл (потокобезопасно),
        // дети сканируются параллельно (до 8 RPC-потоков).
        private void ScanKey(RegistryHive hive, string path, string term, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _searchFound) >= SearchMaxResults) return;
            using var root = RegistryKey.OpenRemoteBaseKey(hive, _computer);
            using var key = string.IsNullOrEmpty(path) ? root : root.OpenSubKey(path, false);
            if (key == null) return;
            if (!string.IsNullOrEmpty(path))
            {
                var leaf = path.Substring(path.LastIndexOf('\\') + 1);
                if (leaf.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    EmitHit(new SearchHit { Hive = hive, Path = path, Kind = "раздел", Display = leaf, Data = "" });
            }
            string[] vnames;
            try { vnames = key.GetValueNames(); } catch { vnames = Array.Empty<string>(); }
            foreach (var vn in vnames)
            {
                ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _searchFound) >= SearchMaxResults) return;
                string disp = string.IsNullOrEmpty(vn) ? "(По умолчанию)" : vn;
                bool nameHit = disp.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
                string kind = "", data = "";
                bool dataHit = false;
                try
                {
                    var k = key.GetValueKind(vn);
                    kind = KindName(k);
                    var raw = key.GetValue(vn);
                    if (k == RegistryValueKind.String || k == RegistryValueKind.ExpandString ||
                        k == RegistryValueKind.MultiString)
                    {
                        data = raw is string[] arr ? string.Join("; ", arr) : (raw?.ToString() ?? "");
                        if (data.Length > 300) data = data.Substring(0, 300) + "…";
                        dataHit = data.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    else data = FormatValue(raw, k);
                }
                catch { kind = "?"; data = "(не прочитано)"; }
                if (nameHit || dataHit)
                    EmitHit(new SearchHit { Hive = hive, Path = path, ValueName = vn, Kind = kind, Display = disp, Data = data });
            }
            string[] subs;
            try { subs = key.GetSubKeyNames(); } catch { return; }
            int s = Interlocked.Increment(ref _searchScanned);
            if (s % 500 == 0)
                SetStatus($"Поиск «{_searchTerm}»: просканировано {s}, найдено {Volatile.Read(ref _searchFound)}…");
            var opts = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct };
            Parallel.ForEach(subs, opts, sub =>
            {
                ScanKey(hive, string.IsNullOrEmpty(path) ? sub : path + "\\" + sub, term, ct);
            });
        }

        private void EmitHit(SearchHit m)
        {
            int f = Interlocked.Increment(ref _searchFound);
            if (f > SearchMaxResults || IsDisposed) return;
            try { BeginInvoke(new Action<SearchHit>(AddResultRow), m); } catch { }
        }

        private void AddResultRow(SearchHit m)
        {
            if (IsDisposed || !_results.Visible) return;
            int idx = _results.Rows.Add(
                FullPath(new NodeTag { Hive = m.Hive, Path = m.Path }), m.Display, m.Kind, m.Data);
            _results.Rows[idx].Tag = new ResultTag { Hive = m.Hive, Path = m.Path, ValueName = m.ValueName };
        }

        // Даблклик по результату: назад к дереву, раскрываем цепочку, подсвечиваем.
        private async Task NavigateToResultAsync(DataGridViewRow row)
        {
            var tag = row.Tag as ResultTag;
            if (tag == null) return;
            ExitSearchMode();
            string hiveName = tag.Hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_USERS";
            TreeNode? node = null;
            foreach (TreeNode n in _tree.Nodes)
                if (string.Equals(n.Text, hiveName, StringComparison.OrdinalIgnoreCase)) { node = n; break; }
            if (node == null) return;
            node.Expand();
            if (!string.IsNullOrEmpty(tag.Path))
            {
                foreach (var seg in tag.Path.Split('\\'))
                {
                    var ntag = node.Tag as NodeTag;
                    if (ntag == null) return;
                    var (subs, error) = await Task.Run(() => TryLoadSubKeys(ntag));
                    if (IsDisposed) return;
                    if (error != null) { SetStatus($"Не раскрыть {seg}: {error}"); return; }
                    FillNode(node, ntag, subs);
                    TreeNode? next = null;
                    foreach (TreeNode c in node.Nodes)
                        if (string.Equals(c.Text, seg, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
                    if (next == null) { SetStatus("Раздел не найден — возможно, его уже удалили."); return; }
                    node = next;
                    node.Expand();
                }
            }
            _tree.SelectedNode = node;
            node.EnsureVisible();
            var finalTag = node.Tag as NodeTag;
            if (finalTag != null) await LoadValuesAsync(finalTag);
            if (!string.IsNullOrEmpty(tag.ValueName))
            {
                foreach (ListViewItem it in _values.Items)
                {
                    string nm = it.Text == "(По умолчанию)" ? "" : it.Text;
                    if (string.Equals(nm, tag.ValueName, StringComparison.Ordinal))
                    {
                        it.Selected = true;
                        it.Focused = true;
                        it.EnsureVisible();
                        break;
                    }
                }
            }
        }

        // === Редактирование как в regedit ===
        private NodeTag? SelectedValuesTag() => _tree.SelectedNode?.Tag as NodeTag;

        private void OnTreeMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var node = _tree.GetNodeAt(e.Location);
            if (node == null || !(node.Tag is NodeTag tag)) return;
            _tree.SelectedNode = node;
            var menu = new Helpers.ThemedContextMenu();
            menu.AddHeader("Раздел");
            menu.AddItem("Создать раздел...", "add", () => _ = CreateKeyAsync(tag, node));
            if (!string.IsNullOrEmpty(tag.Path))
            {
                menu.AddItem("Переименовать раздел", "list", () => _ = RenameKeyAsync(tag, node));
                menu.AddItem("Удалить раздел...", "delete", () => _ = DeleteKeyAsync(tag, node));
            }
            menu.Show(this, Control.MousePosition);
        }

        private void OnValuesMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var tag = SelectedValuesTag();
            if (tag == null) return;
            var hit = _values.HitTest(e.Location);
            if (hit.Item != null) hit.Item.Selected = true;
            var menu = new Helpers.ThemedContextMenu();
            menu.AddHeader("Параметр");
            if (_values.SelectedItems.Count > 0)
            {
                menu.AddItem("Изменить...", "list", () => _ = EditSelectedValueAsync());
                menu.AddItem("Переименовать", "list", () => _ = RenameSelectedValueAsync());
                menu.AddItem("Удалить", "delete", () => _ = DeleteSelectedValueAsync());
                menu.AddSeparator();
            }
            menu.AddSubmenu("Создать", "add", sub =>
            {
                sub.AddItem("Строковый параметр", "list", () => _ = CreateValueAsync(tag, RegistryValueKind.String));
                sub.AddItem("Расширяемый строковый параметр", "list", () => _ = CreateValueAsync(tag, RegistryValueKind.ExpandString));
                sub.AddItem("Двоичный параметр", "list", () => _ = CreateValueAsync(tag, RegistryValueKind.Binary));
                sub.AddItem("Параметр DWORD (32 бита)", "list", () => _ = CreateValueAsync(tag, RegistryValueKind.DWord));
                sub.AddItem("Параметр QWORD (64 бита)", "list", () => _ = CreateValueAsync(tag, RegistryValueKind.QWord));
                sub.AddItem("Мультистроковый параметр", "list", () => _ = CreateValueAsync(tag, RegistryValueKind.MultiString));
            });
            menu.Show(this, Control.MousePosition);
        }

        private (string name, RegistryValueKind kind, object? raw)? ReadOneValueSync(NodeTag tag, string name)
        {
            try
            {
                using var root = RegistryKey.OpenRemoteBaseKey(tag.Hive, _computer);
                using var key = string.IsNullOrEmpty(tag.Path) ? root : root.OpenSubKey(tag.Path, false);
                if (key == null) return null;
                return (name, key.GetValueKind(name), key.GetValue(name));
            }
            catch { return null; }
        }

        private async Task EditSelectedValueAsync()
        {
            if (_values.SelectedItems.Count == 0) return;
            var it = _values.SelectedItems[0];
            string name = it.Text == "(По умолчанию)" ? "" : it.Text;
            var tag = SelectedValuesTag();
            if (tag == null) return;
            var read = await Task.Run(() => ReadOneValueSync(tag, name));
            if (read == null)
            {
                MessageBox.Show(this, "Не удалось прочитать параметр.", "Изменение",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!ShowValueEditor(name, read.Value.kind, read.Value.raw, out var nw)) return;
            bool ok = await Task.Run(() => WriteValue(tag, name, read.Value.kind, nw));
            if (!ok)
            {
                MessageBox.Show(this, "Нет доступа для записи. Нужны права на изменение реестра цели.",
                    "Изменение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Logger.Info($"RemoteRegistry set {FullPath(tag)}\\{(name == "" ? "(Default)" : name)} on {_computer}");
            await LoadValuesAsync(tag);
        }

        private async Task RenameSelectedValueAsync()
        {
            if (_values.SelectedItems.Count == 0) return;
            var it = _values.SelectedItems[0];
            string oldName = it.Text == "(По умолчанию)" ? "" : it.Text;
            if (oldName == "")
            {
                MessageBox.Show(this, "Параметр по умолчанию переименовать нельзя.", "Переименование",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var tag = SelectedValuesTag();
            if (tag == null) return;
            string? nn = PromptDialog.Show(this, "Переименование", "Новое имя:", oldName);
            if (string.IsNullOrEmpty(nn) || nn == oldName) return;
            if (nn.Contains('\\'))
            {
                MessageBox.Show(this, "Имя не должно содержать \\.", "Переименование",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool ok = await Task.Run(() =>
            {
                var read = ReadOneValueSync(tag, oldName);
                if (read == null) return false;
                if (!WriteValue(tag, nn, read.Value.kind, read.Value.raw)) return false;
                return DeleteValue(tag, oldName);
            });
            if (!ok)
            {
                MessageBox.Show(this, "Нет доступа для записи.", "Переименование",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Logger.Info($"RemoteRegistry rename {FullPath(tag)}\\{oldName} -> {nn} on {_computer}");
            await LoadValuesAsync(tag);
        }

        private async Task DeleteSelectedValueAsync()
        {
            if (_values.SelectedItems.Count == 0) return;
            var it = _values.SelectedItems[0];
            string name = it.Text == "(По умолчанию)" ? "" : it.Text;
            var tag = SelectedValuesTag();
            if (tag == null) return;
            string disp = name == "" ? "(По умолчанию)" : $"'{name}'";
            var r = MessageBox.Show(this, $"Удалить параметр {disp} в\n{FullPath(tag)}?",
                "Удаление", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;
            bool ok = await Task.Run(() => DeleteValue(tag, name));
            if (!ok)
            {
                MessageBox.Show(this, "Нет доступа для записи.", "Удаление",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Logger.Info($"RemoteRegistry delete value {FullPath(tag)}\\{disp} on {_computer}");
            await LoadValuesAsync(tag);
        }

        private async Task CreateValueAsync(NodeTag tag, RegistryValueKind kind)
        {
            string? name = PromptDialog.Show(this, "Новый параметр",
                $"Имя ({KindName(kind)}):", "Новый параметр");
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0 || name.Contains('\\'))
            {
                MessageBox.Show(this, "Пустое имя или символ \\ недопустимы.", "Новый параметр",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            object? init = kind switch
            {
                RegistryValueKind.DWord => (object)0,
                RegistryValueKind.QWord => (object)0L,
                RegistryValueKind.Binary => (object)new byte[0],
                RegistryValueKind.MultiString => (object)new string[0],
                _ => (object)""
            };
            if (!ShowValueEditor(name, kind, init, out var data)) return;
            bool ok = await Task.Run(() => WriteValue(tag, name, kind, data));
            if (!ok)
            {
                MessageBox.Show(this, "Нет доступа для записи.", "Новый параметр",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Logger.Info($"RemoteRegistry create value {FullPath(tag)}\\{name} ({KindName(kind)}) on {_computer}");
            await LoadValuesAsync(tag);
        }

        private async Task CreateKeyAsync(NodeTag parentTag, TreeNode parentNode)
        {
            string? name = PromptDialog.Show(this, "Новый раздел", "Имя раздела:", "Новый раздел");
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0 || name.Contains('\\'))
            {
                MessageBox.Show(this, "Пустое имя или символ \\ недопустимы.", "Новый раздел",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string full = string.IsNullOrEmpty(parentTag.Path) ? name : parentTag.Path + "\\" + name;
            bool ok = await Task.Run(() =>
            {
                try
                {
                    using var root = RegistryKey.OpenRemoteBaseKey(parentTag.Hive, _computer);
                    using var k = root.CreateSubKey(full);
                    return k != null;
                }
                catch (Exception ex) { Logger.Warn($"RemoteRegistry create key failed: {ex.Message}"); return false; }
            });
            if (!ok)
            {
                MessageBox.Show(this, "Нет доступа для записи.", "Новый раздел",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Logger.Info($"RemoteRegistry create key {FullPath(parentTag)}\\{name} on {_computer}");
            await RefreshNodeChildrenAsync(parentNode);
            parentNode.Expand();
        }

        private async Task RenameKeyAsync(NodeTag tag, TreeNode node)
        {
            string oldLeaf = tag.Path.Substring(tag.Path.LastIndexOf('\\') + 1);
            string? nn = PromptDialog.Show(this, "Переименование раздела", "Новое имя:", oldLeaf);
            if (nn == null) return;
            nn = nn.Trim();
            if (nn.Length == 0 || nn == oldLeaf || nn.Contains('\\'))
            {
                if (nn.Contains('\\'))
                    MessageBox.Show(this, "Имя не должно содержать \\.", "Переименование",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string parentPath = tag.Path.Contains('\\') ? tag.Path.Substring(0, tag.Path.LastIndexOf('\\')) : "";
            string dst = string.IsNullOrEmpty(parentPath) ? nn : parentPath + "\\" + nn;
            SetStatus($"Переименование {oldLeaf} → {nn}...");
            bool ok = await Task.Run(() =>
            {
                if (!CopyKeyRecursive(tag.Hive, tag.Path, dst)) return false;
                try
                {
                    using var root = RegistryKey.OpenRemoteBaseKey(tag.Hive, _computer);
                    root.DeleteSubKeyTree(tag.Path);
                    return true;
                }
                catch (Exception ex) { Logger.Warn($"RemoteRegistry rename/delete src failed: {ex.Message}"); return false; }
            });
            if (!ok)
            {
                MessageBox.Show(this, "Не удалось переименовать (нет доступа?).", "Переименование",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                await LoadValuesAsync(tag);
                return;
            }
            Logger.Info($"RemoteRegistry rename key {FullPath(tag)} -> {dst} on {_computer}");
            var parent = node.Parent;
            if (parent?.Tag is NodeTag ptag) await RefreshNodeChildrenAsync(parent);
            SetStatus("Раздел переименован.");
        }

        private async Task DeleteKeyAsync(NodeTag tag, TreeNode node)
        {
            var r = MessageBox.Show(this,
                $"Удалить раздел\n{FullPath(tag)}\n\nВКЛЮЧАЯ все подразделы и параметры?",
                "Удаление раздела", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;
            bool ok = await Task.Run(() =>
            {
                try
                {
                    using var root = RegistryKey.OpenRemoteBaseKey(tag.Hive, _computer);
                    root.DeleteSubKeyTree(tag.Path);
                    return true;
                }
                catch (Exception ex) { Logger.Warn($"RemoteRegistry delete key failed: {ex.Message}"); return false; }
            });
            if (!ok)
            {
                MessageBox.Show(this, "Нет доступа для записи.", "Удаление раздела",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Logger.Info($"RemoteRegistry delete key {FullPath(tag)} on {_computer}");
            var parent = node.Parent;
            node.Remove();
            if (parent?.Tag is NodeTag ptag)
            {
                _tree.SelectedNode = parent;
                await RefreshNodeChildrenAsync(parent);
                await LoadValuesAsync(ptag);
            }
        }

        private bool WriteValue(NodeTag tag, string name, RegistryValueKind kind, object? data)
        {
            try
            {
                using var root = RegistryKey.OpenRemoteBaseKey(tag.Hive, _computer);
                if (string.IsNullOrEmpty(tag.Path))
                {
                    root.SetValue(name, data ?? "", kind);
                    return true;
                }
                using var key = root.OpenSubKey(tag.Path, true);
                if (key == null) return false;
                key.SetValue(name, data ?? "", kind);
                return true;
            }
            catch (Exception ex) { Logger.Warn($"RemoteRegistry write failed: {ex.Message}"); return false; }
        }

        private bool DeleteValue(NodeTag tag, string name)
        {
            try
            {
                using var root = RegistryKey.OpenRemoteBaseKey(tag.Hive, _computer);
                if (string.IsNullOrEmpty(tag.Path))
                {
                    root.DeleteValue(name, false);
                    return true;
                }
                using var key = root.OpenSubKey(tag.Path, true);
                if (key == null) return false;
                key.DeleteValue(name, false);
                return true;
            }
            catch (Exception ex) { Logger.Warn($"RemoteRegistry delete value failed: {ex.Message}"); return false; }
        }

        private bool CopyKeyRecursive(RegistryHive hive, string src, string dst)
        {
            try
            {
                using var root = RegistryKey.OpenRemoteBaseKey(hive, _computer);
                using var srcKey = root.OpenSubKey(src, false);
                if (srcKey == null) return false;
                using var dstKey = root.CreateSubKey(dst);
                if (dstKey == null) return false;
                foreach (var vn in srcKey.GetValueNames())
                {
                    try { dstKey.SetValue(vn, srcKey.GetValue(vn)!, srcKey.GetValueKind(vn)); } catch { }
                }
                foreach (var sn in srcKey.GetSubKeyNames())
                {
                    if (!CopyKeyRecursive(hive, src + "\\" + sn, dst + "\\" + sn)) return false;
                }
                return true;
            }
            catch (Exception ex) { Logger.Warn($"RemoteRegistry copy key failed: {ex.Message}"); return false; }
        }

        private async Task RefreshNodeChildrenAsync(TreeNode node)
        {
            var tag = node.Tag as NodeTag;
            if (tag == null) return;
            var (subs, error) = await Task.Run(() => TryLoadSubKeys(tag));
            if (IsDisposed) return;
            node.Nodes.Clear();
            if (error != null)
            {
                node.Nodes.Add(new TreeNode($"(нет доступа: {error})") { Tag = DummyTag });
                return;
            }
            FillNode(node, tag, subs);
        }

        // Диалог изменения значения: редактор под тип (строка / dec+hex /
        // hex-байты / много строк). Возвращает false при отмене.
        private bool ShowValueEditor(string valueName, RegistryValueKind kind, object? current, out object? result)
        {
            result = null;
            string dispName = string.IsNullOrEmpty(valueName) ? "(По умолчанию)" : valueName;
            bool isQword = kind == RegistryValueKind.QWord;
            bool isDword = kind == RegistryValueKind.DWord;
            bool isNum = isDword || isQword;
            bool isBin = kind == RegistryValueKind.Binary;
            bool isMulti = kind == RegistryValueKind.MultiString;
            int editH = (isBin || isMulti) ? 140 : 26;
            using var dlg = new Form
            {
                Text = $"Изменение: {dispName}",
                ClientSize = new Size(470, 96 + editH),
                StartPosition = FormStartPosition.CenterParent,
                Font = new Font("Segoe UI", 9F),
                BackColor = ShadowRDP.Helpers.UiTheme.DialogBackground,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };
            var lbl = new Label
            {
                Text = $"Значение ({KindName(kind)}):",
                Location = new Point(16, 14),
                AutoSize = true,
                ForeColor = ShadowRDP.Helpers.UiTheme.Foreground
            };
            dlg.Controls.Add(lbl);
            var tb = new TextBox
            {
                Location = new Point(16, 40),
                Size = new Size(438, editH),
                Font = new Font("Consolas", 10F),
                Multiline = isBin || isMulti,
                ScrollBars = (isBin || isMulti) ? ScrollBars.Both : ScrollBars.None,
                WordWrap = false
            };
            RadioButton? rbDec = null, rbHex = null;
            if (isNum)
            {
                ulong uv = 0;
                try
                {
                    if (current != null)
                        uv = isQword ? Convert.ToUInt64(current) : Convert.ToUInt32(current);
                }
                catch { }
                tb.Text = uv.ToString();
                rbDec = new RadioButton { Text = "Десятичная", Location = new Point(16, 72), AutoSize = true, Checked = true };
                rbHex = new RadioButton { Text = "Шестнадцатеричная", Location = new Point(140, 72), AutoSize = true };
                var tbRef = tb;
                var isQ = isQword;
                rbDec.CheckedChanged += (s, e) =>
                {
                    if (!rbDec.Checked) return;
                    try
                    {
                        string t = tbRef.Text.Trim().ToLowerInvariant().Replace("0x", "");
                        ulong v = string.IsNullOrEmpty(t) ? 0 : Convert.ToUInt64(t, 16);
                        tbRef.Text = v.ToString();
                    }
                    catch { }
                };
                rbHex.CheckedChanged += (s, e) =>
                {
                    if (!rbHex.Checked) return;
                    try
                    {
                        ulong v = 0;
                        ulong.TryParse(tbRef.Text.Trim(), out v);
                        tbRef.Text = isQ ? v.ToString("x16") : v.ToString("x8");
                    }
                    catch { }
                };
                dlg.Controls.Add(rbDec);
                dlg.Controls.Add(rbHex);
            }
            else if (isBin)
            {
                tb.Text = current is byte[] bb && bb.Length > 0
                    ? BitConverter.ToString(bb).Replace("-", " ") : "";
            }
            else if (isMulti)
            {
                tb.Lines = current as string[] ?? new string[0];
            }
            else
            {
                tb.Font = new Font("Segoe UI", 9F);
                tb.Text = current?.ToString() ?? "";
            }
            dlg.Controls.Add(tb);
            var btnFont = new Font("Segoe UI Semibold", 9F);
            int bw = TextRenderer.MeasureText("Отмена", btnFont).Width + 28;
            var okBtn = Helpers.UiButtons.MakeCompactFilled("ОК", bw);
            okBtn.Location = new Point(470 - 16 - bw * 2 - 8, 62 + editH);
            var cancelBtn = Helpers.UiButtons.MakeCompactFilled("Отмена", bw);
            cancelBtn.Location = new Point(470 - 16 - bw, 62 + editH);
            dlg.Controls.Add(okBtn);
            dlg.Controls.Add(cancelBtn);
            object? outVal = null;
            bool done = false;
            okBtn.Click += (s, e) =>
            {
                try
                {
                    outVal = ParseEditorValue(kind, tb.Text, rbHex?.Checked == true);
                    done = true;
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dlg, ex.Message, "Значение",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            cancelBtn.Click += (s, e) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            var r = ShadowRDP.Helpers.UiModal.ShowDimmed(this, dlg);
            result = outVal;
            return done && r == DialogResult.OK;
        }

        private static object ParseEditorValue(RegistryValueKind kind, string text, bool hex)
        {
            switch (kind)
            {
                case RegistryValueKind.DWord:
                {
                    uint u;
                    if (hex)
                    {
                        string t = text.Trim().ToLowerInvariant().Replace("0x", "").Replace(" ", "");
                        if (!uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out u))
                            throw new Exception("Некорректное HEX-число (0..FFFFFFFF).");
                    }
                    else if (!uint.TryParse(text.Trim(), out u))
                        throw new Exception("Некорректное число (0..4294967295).");
                    return unchecked((int)u);
                }
                case RegistryValueKind.QWord:
                {
                    ulong u;
                    if (hex)
                    {
                        string t = text.Trim().ToLowerInvariant().Replace("0x", "").Replace(" ", "");
                        if (!ulong.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out u))
                            throw new Exception("Некорректное HEX-число (0..FFFFFFFFFFFFFFFF).");
                    }
                    else if (!ulong.TryParse(text.Trim(), out u))
                        throw new Exception("Некорректное число.");
                    return unchecked((long)u);
                }
                case RegistryValueKind.Binary:
                {
                    string t = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
                    if (t.Length == 0) return new byte[0];
                    if (t.Length % 2 != 0 || t.Any(c => !"0123456789abcdefABCDEF".Contains(c)))
                        throw new Exception("HEX-байты: чётное число цифр 0-9, A-F.");
                    var buf = new byte[t.Length / 2];
                    for (int i = 0; i < buf.Length; i++)
                        buf[i] = Convert.ToByte(t.Substring(i * 2, 2), 16);
                    return buf;
                }
                case RegistryValueKind.MultiString:
                    return text.Replace("\r\n", "\n").Split('\n');
                default:
                    return text;
            }
        }

        private async Task LoadValuesAsync(NodeTag tag)
        {
            var path = FullPath(tag);
            SetStatus($"{path} — чтение...");
            var rows = await Task.Run(() => ReadValues(tag));
            if (IsDisposed) return;
            _values.BeginUpdate();
            try
            {
                _values.Items.Clear();
                foreach (var (name, type, data) in rows)
                {
                    var item = new ListViewItem(string.IsNullOrEmpty(name) ? "(По умолчанию)" : name);
                    item.SubItems.Add(type);
                    item.SubItems.Add(data);
                    _values.Items.Add(item);
                }
            }
            finally
            {
                _values.EndUpdate();
            }
            SetStatus($"{path} — значений: {rows.Count}.");
        }

        private System.Collections.Generic.List<(string name, string type, string data)> ReadValues(NodeTag tag)
        {
            var rows = new System.Collections.Generic.List<(string name, string type, string data)>();
            try
            {
                using var root = RegistryKey.OpenRemoteBaseKey(tag.Hive, _computer);
                using var key = string.IsNullOrEmpty(tag.Path) ? root : root.OpenSubKey(tag.Path, false);
                if (key == null) return rows;
                foreach (var vn in key.GetValueNames())
                {
                    try
                    {
                        var kind = key.GetValueKind(vn);
                        rows.Add((vn, KindName(kind), FormatValue(key.GetValue(vn), kind)));
                    }
                    catch
                    {
                        rows.Add((vn, "?", "(не прочитано)"));
                    }
                }
            }
            catch { }
            rows.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            return rows;
        }

        private static string KindName(RegistryValueKind kind) => kind switch
        {
            RegistryValueKind.String => "REG_SZ",
            RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
            RegistryValueKind.Binary => "REG_BINARY",
            RegistryValueKind.DWord => "REG_DWORD",
            RegistryValueKind.MultiString => "REG_MULTI_SZ",
            RegistryValueKind.QWord => "REG_QWORD",
            _ => kind.ToString()
        };

        private static string FormatValue(object? v, RegistryValueKind kind)
        {
            try
            {
                if (v == null) return "(значение не задано)";
                switch (kind)
                {
                    case RegistryValueKind.DWord:
                        int i = Convert.ToInt32(v);
                        return $"{i} (0x{i:x8})";
                    case RegistryValueKind.QWord:
                        long l = Convert.ToInt64(v);
                        return $"{l} (0x{l:x16})";
                    case RegistryValueKind.Binary:
                        var bytes = (byte[])v;
                        var hex = BitConverter.ToString(bytes.Take(32).ToArray()).Replace("-", " ");
                        return bytes.Length > 32 ? $"{hex} … ({bytes.Length} байт)" : $"{hex} ({bytes.Length} байт)";
                    case RegistryValueKind.MultiString:
                        return string.Join("; ", (string[])v);
                    default:
                        var s = v.ToString() ?? "";
                        return s.Length > 512 ? s.Substring(0, 512) + "…" : s;
                }
            }
            catch { return "(не прочитано)"; }
        }

        private static string FullPath(NodeTag tag) =>
            (tag.Hive == RegistryHive.LocalMachine ? "HKLM" : "HKU") +
            (string.IsNullOrEmpty(tag.Path) ? "" : "\\" + tag.Path);

        private static string ShortError(Exception ex)
        {
            var m = ex.Message ?? "";
            var nl = m.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) m = m.Substring(0, nl);
            return m.Length > 90 ? m.Substring(0, 90) + "…" : m;
        }

        private void SetStatus(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(SetStatus), text); } catch { } return; }
            _statusLabel.Text = text;
        }

        private void BeginInvokeSafe(Action a)
        {
            if (IsDisposed) return;
            try { BeginInvoke(a); } catch { }
        }
    }
}
