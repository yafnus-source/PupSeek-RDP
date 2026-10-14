using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.7.0 — KeePass vault manager: browse groups, view/save/delete
    // entries. Opened from the tray menu ("Пароли…"). If the vault isn't
    // open yet, the unlock dialog appears first.
    //
    // v4.3.3 — redesigned in the FTP sample-modal style (hero + section
    // cards + footer): gradient hero band instead of the blue header bar,
    // group tree and entries grid each live in a rounded card, toolbar
    // actions moved to the footer (left: entry actions, right: lock/close).
    // All logic (unlock flow, CRUD, persistence) is untouched.
    public class KeePassVaultForm : Form
    {
        private static KeePassVaultForm? _instance;

        public static KeePassVaultForm Instance
        {
            get
            {
                if (_instance == null || _instance.IsDisposed)
                    _instance = new KeePassVaultForm();
                return _instance;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_instance == this) _instance = null;
            base.OnFormClosed(e);
        }

        private readonly Controls.ScrollHiddenTreeView _tree;
        private readonly DataGridView _grid;
        private readonly Label _status;
        private readonly Label _btnEdit;
        private readonly Label _btnDelete;
        private readonly Label _btnCopy;
        // v3.7.1 — split is a field so SplitterDistance can be persisted
        // (Load on form Load, Save on SplitterMoved + FormClosing).
        private readonly SplitContainer _split;
        private readonly System.Windows.Forms.Timer _saveDebounce = new() { Interval = 400 };

        // Column-width persistence key — shared column_widths.json namespace.
        private const string ColumnWidthsFormKey = "keepassVault";
        // v3.7.1 — splitter-distance persistence key. Same column_widths.json
        // file, separate namespace to avoid clashing with the column-width
        // dictionary schema.
        private const string SplitDistanceFormKey = "keepassVaultSplit";

        public KeePassVaultForm()
        {
            Text = "Пароли — PupSeekRDP";
            ClientSize = new Size(880, 520);
            MinimumSize = new Size(720, 420);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = UiTheme.DialogBackground;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); } };

            // ——— 1. Hero (gradient band with icon, title, subtitle, ✕) ———
            var hero = UiModal.Hero("Пароли", "хранилище KeePass (.kdbx)", "shield");

            // ——— 2. Content: group tree | entries grid, each in its own card ———
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = UiTheme.DialogBackground };
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 260,
                SplitterWidth = 5,
                BackColor = UiTheme.DialogBackground
            };
            // Отступ карточек от внешних границ 5px; между карточками тоже 5px:
            // внутренние края без паддинга, зазор даёт сам сплиттер.
            // Фон панелей в цвет страницы: иначе при живом ресайзе сплиттера
            // в неприкрытых щелях светится серая подложка и читается как
            // «лишние границы».
            _split.Panel1.Padding = new Padding(5, 5, 0, 5);
            _split.Panel2.Padding = new Padding(0, 5, 5, 5);
            _split.Panel1.BackColor = UiTheme.DialogBackground;
            _split.Panel2.BackColor = UiTheme.DialogBackground;
            content.Controls.Add(_split);

            var cardTree = UiModal.Card(0, 0, 100, 100, "Группы", "list");
            cardTree.Dock = DockStyle.Fill;
            var cardGrid = UiModal.Card(0, 0, 100, 100, "Записи", "key");
            cardGrid.Dock = DockStyle.Fill;
            _split.Panel1.Controls.Add(cardTree);
            _split.Panel2.Controls.Add(cardGrid);
            // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
            foreach (var c in new Control[] { cardTree, cardGrid })
            { try { c.Region?.Dispose(); c.Region = null; } catch { } }


            _tree = new Controls.ScrollHiddenTreeView
            {
                BorderStyle = BorderStyle.None,
                HideSelection = false,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(0x1E, 0x1E, 0x1E),
                Font = new Font("Segoe UI", 9F),
                ItemHeight = 22
            };
            cardTree.Controls.Add(_tree);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachTreeView(_tree);
            RoundCorners(_tree, 7);

            // === Entries grid (borderless, themed — same as Open Files) ===
            // v3.7.1: AutoSizeColumnsMode switched from Fill to None. In Fill
            // mode the grid redistributes column widths on every form resize
            // using FillWeight as the proportion — user drags were captured
            // as Width but then overridden by the next Fill-layout pass, so
            // column widths never actually persisted. None mode keeps Width
            // stable; columns the user doesn't touch stay at their defaults.
            _grid = new Controls.DoubleBufferedDataGridView
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                BorderStyle = BorderStyle.None,
                BackgroundColor = UiTheme.Card
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Имя", Width = 400, MinimumWidth = 90 });
            // v3.9.5 — «Логин» тянется на остаток ширины: суммарная ширина
            // колонок всегда равна клиенту → нижняя (горизонтальная) полоса
            // не появляется. Ширина «Имени» по-прежнему persists (None-режим).
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Login", HeaderText = "Логин", MinimumWidth = 90, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            ShadowRDP.Helpers.UiGridStyle.ApplyUnified(_grid, 36, 36);
            cardGrid.Controls.Add(_grid);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachGrid(_grid);
            RoundCorners(_grid, 7);

            // Children are laid out explicitly (no anchors): cards are docked
            // and resize with the sizable window, children follow the card.
            // Полный Invalidate на каждый шаг ресайза: гасит застойные
            // штрихи-призраки при живом движении сплиттера/колонок.
            cardTree.Resize += (s, e) => { FitCardChild(cardTree, _tree); try { cardTree.Invalidate(true); } catch { } };
            cardGrid.Resize += (s, e) => { FitCardChild(cardGrid, _grid); try { cardGrid.Invalidate(true); } catch { } };
            _grid.ColumnWidthChanged += (s, e) => { try { cardGrid.Invalidate(true); } catch { } };
            FitCardChild(cardTree, _tree);
            FitCardChild(cardGrid, _grid);

            // ——— 3. Status bar (thin strip above the footer) ———
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = UiTheme.DialogCard };
            statusPanel.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, statusPanel.Width, 0); };
            _status = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.MutedForeground,
                Font = new Font("Segoe UI", 8.5F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 0, 0)
            };
            statusPanel.Controls.Add(_status);

            // ——— 4. Footer with action buttons ———
            // Left: entry actions. Right: lock + close (positioned on Resize,
            // the window is sizable — same pattern as the old toolbar).
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var btnFont = new Font("Segoe UI Semibold", 9F);
            int w(string text) => TextRenderer.MeasureText(text, btnFont).Width + 28;

            var btnAdd = UiButtons.MakeCompactFilled("Новая запись", w("Новая запись"));
            btnAdd.Location = new Point(16, 15);
            var btnEdit = UiButtons.MakeCompactFilled("Изменить", w("Изменить"));
            btnEdit.Location = new Point(16 + btnAdd.Width + 8, 15);
            var btnDelete = UiButtons.MakeCompactFilled("Удалить", w("Удалить"), danger: true);
            btnDelete.Location = new Point(btnEdit.Left + btnEdit.Width + 8, 15);
            var btnCopy = UiButtons.MakeCompactFilled("Копировать пароль", w("Копировать пароль"));
            btnCopy.Location = new Point(btnDelete.Left + btnDelete.Width + 8, 15);

            var btnLock = UiButtons.MakeCompactFilled("Заблокировать", w("Заблокировать"));
            var btnClose = UiButtons.MakeCompactFilled("Закрыть", w("Закрыть"));
            footer.Resize += (s, e) =>
            {
                btnLock.Left = footer.ClientSize.Width - 16 - btnClose.Width - 8 - btnLock.Width;
                btnLock.Top = 15;
                btnClose.Left = footer.ClientSize.Width - 16 - btnClose.Width;
                btnClose.Top = 15;
            };

            _btnEdit = btnEdit;
            _btnDelete = btnDelete;
            _btnCopy = btnCopy;

            footer.Controls.AddRange(new Control[] { btnAdd, btnEdit, btnDelete, btnCopy, btnLock, btnClose });

            btnAdd.Click += (_, _) => AddEntry();
            btnEdit.Click += (_, _) => EditEntry();
            btnDelete.Click += (_, _) => DeleteEntry();
            btnCopy.Click += (_, _) => CopyPassword();
            btnLock.Click += (_, _) => LockVault();
            btnClose.Click += (_, _) => Close();

            // Dock order: content Fill first, then hero Top, then the two
            // Bottom strips (footer added last docks first → very bottom,
            // status above it). Same rule as the FTP sample modal.
            Controls.Add(content);
            Controls.Add(hero);
            Controls.Add(statusPanel);
            Controls.Add(footer);

            var dpiScale = UiModal.ApplyDpiScale(this);
            if (dpiScale > 1.001f)
                ClientSize = new Size((int)Math.Round(ClientSize.Width * dpiScale), (int)Math.Round(ClientSize.Height * dpiScale));

            _tree.AfterSelect += (_, _) => ReloadEntries();
            _grid.SelectionChanged += (_, _) =>
            {
                var has = _grid.SelectedRows.Count > 0;
                _btnEdit.Enabled = has;
                _btnDelete.Enabled = has;
                _btnCopy.Enabled = has;
            };
            // v3.9.5 — даблклик = редактирование записи (было копирование
            // пароля; оно осталось на кнопке и в ПКМ).
            _grid.CellMouseDoubleClick += (_, _) => EditEntry();

            // v3.9.0 — right-click menu shown as a themed popup.
            _grid.CellMouseDown += (s, args) =>
            {
                if (args.Button != MouseButtons.Right) return;
                if (args.RowIndex < 0) return;
                if (!_grid.Rows[args.RowIndex].Selected)
                {
                    _grid.ClearSelection();
                    _grid.Rows[args.RowIndex].Selected = true;
                }
                var has = _grid.SelectedRows.Count > 0;
                var menu = new Helpers.ThemedContextMenu();
                menu.AddHeader("Хранилище паролей");
                menu.AddItem("Новая запись", "add", () => AddEntry());
                if (has)
                {
                    menu.AddItem("Дублировать запись", "copy", () => DuplicateEntry());
                    menu.AddItem("Изменить", "info", () => EditEntry());
                    menu.AddItem("Копировать пароль", "copy", () => CopyPassword());
                    menu.AddItem("Удалить", "delete", () => DeleteEntry());
                }
                menu.Show(this, Control.MousePosition);
            };

            // v3.7.0 — window size + grid column widths persist like the
            // other dialogs; column drag-to-reorder via GridColumnOrder.
            // v3.7.1 — splitter distance also persisted (was hardcoded 260).
            WindowStateStore.Register(this, "keepassVault");
            LoadColumnWidths();
            // SplitterDistance can only be applied after the handle exists and
            // _split has real pixel width — clamp in LoadSplitDistance guards
            // against that, but the assignment itself needs a non-zero Width
            // or the WinForms setter throws.
            Load += (s, e) => LoadSplitDistance();
            FormClosing += (s, e) => { SaveColumnWidths(); SaveSplitDistance(); };
            Resize += (s, e) => { _saveDebounce.Stop(); _saveDebounce.Start(); };
            _grid.ColumnWidthChanged += (s, e) => { _saveDebounce.Stop(); _saveDebounce.Start(); };
            _split.SplitterMoved += (s, e) => { _saveDebounce.Stop(); _saveDebounce.Start(); };
            _saveDebounce.Tick += (s, e) =>
            {
                _saveDebounce.Stop();
                if (IsDisposed) return;
                SaveColumnWidths();
                SaveSplitDistance();
                WindowStateStore.Save(this, "keepassVault");
            };

            Shown += (_, _) => EnsureUnlocked();
        }

        // Child of a card: padded area below the card header (header ends ~y44).
        private static void FitCardChild(Panel card, Control child)
        {
            child.Bounds = new Rectangle(14, 52,
                Math.Max(50, card.ClientSize.Width - 28),
                Math.Max(50, card.ClientSize.Height - 66));
        }

        // Скругление углов (как в FTP-окне): режем Region контрола по
        // скруглённому rect, чтобы прямоугольные углы не торчали за радиус.
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

        private void EnsureUnlocked()
        {
            // v3.7.1 — silent reopen first. If auto-lock fired but the user
            // already typed the master password earlier in this session, the
            // vault reopens transparently via the in-memory SecureString and
            // we go straight to ReloadGroups. Only when no saved key exists
            // (cold start, or after an explicit "Заблокировать") does the
            // unlock dialog appear.
            if (KeePassVault.EnsureReopened())
            {
                ReloadGroups();
                return;
            }
            var cfg = KeePassVault.LoadConfig();
            var res = KeePassUnlockDialog.Show(this, cfg);
            if (res == null)
            {
                Close();
                return;
            }
            var err = KeePassVault.Open(res.DbPath, res.KeyFilePath, res.Password);
            if (err != null)
            {
                MessageBox.Show(this, $"Не удалось открыть базу:\n{err}",
                    "Пароли", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            cfg.DbPath = res.DbPath;
            cfg.KeyFilePath = res.KeyFilePath;
            cfg.AutoUnlockAtStartup = res.AutoUnlockAtStartup;
            KeePassVault.SaveConfig(cfg);
            ReloadGroups();
        }

        private void ReloadGroups()
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            var dbName = Path.GetFileName(KeePassVault.CurrentDbPath);
            var root = new TreeNode(string.IsNullOrEmpty(dbName) ? "База" : dbName)
            {
                Name = ""
            };
            _tree.Nodes.Add(root);
            foreach (var path in KeePassVault.ListGroups())
            {
                AddTreePath(root, path);
            }
            root.Expand();
            _tree.EndUpdate();
            if (root.Nodes.Count > 0)
                _tree.SelectedNode = root.Nodes[0];
            else
            {
                _tree.SelectedNode = root;
                ReloadEntries();
            }
        }

        private static void AddTreePath(TreeNode root, string path)
        {
            var parts = path.Split('/');
            var cur = root;
            foreach (var part in parts)
            {
                var node = cur.Nodes.Cast<TreeNode>()
                    .FirstOrDefault(n => string.Equals(n.Text, part, StringComparison.OrdinalIgnoreCase));
                if (node == null)
                {
                    node = new TreeNode(part) { Name = part };
                    cur.Nodes.Add(node);
                }
                cur = node;
            }
        }

        private string SelectedGroupPath
        {
            get
            {
                var node = _tree.SelectedNode;
                if (node == null) return "";
                var parts = new List<string>();
                var cur = node;
                // Skip the database root node.
                while (cur.Parent != null)
                {
                    parts.Add(cur.Name);
                    cur = cur.Parent;
                }
                parts.Reverse();
                return string.Join("/", parts);
            }
        }

        private void ReloadEntries()
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            var group = SelectedGroupPath;
            _status.Text = KeePassVault.IsOpen
                ? $"База: {KeePassVault.CurrentDbPath} | Группа: {(string.IsNullOrEmpty(group) ? "—" : group)}"
                : "База заблокирована";
            if (string.IsNullOrEmpty(group)) { _grid.ResumeLayout(); return; }
            var entries = KeePassVault.ListEntries(group);
            if (entries == null) { _grid.ResumeLayout(); return; }
            foreach (var e in entries)
            {
                var idx = _grid.Rows.Add(e.Name, e.Login);
                _grid.Rows[idx].Tag = e;
            }
            _grid.ResumeLayout();
            var has = _grid.SelectedRows.Count > 0;
            _btnEdit.Enabled = has;
            _btnDelete.Enabled = has;
            _btnCopy.Enabled = has;
        }

        // v3.9.5 — брать именно ВЫБРАННУЮ строку: Rows[0] всегда
        // отдавал первую запись (в таблице 172.168.218.54, в редакторе vtb).
        private KeePassVault.VaultEntry? SelectedEntry =>
            _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as KeePassVault.VaultEntry : null;

        private void AddEntry()
        {
            var group = SelectedGroupPath;
            if (string.IsNullOrEmpty(group))
            {
                MessageBox.Show(this, "Выберите группу для новой записи.",
                    "Пароли", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var (name, login, password, notes) = EntryEditor.Show(this, "Новая запись", group, "", "", "", "");
            if (name == null) return;
            var err = KeePassVault.SaveLogin(group, name, login ?? "", password ?? "", notes);
            if (err != null)
            {
                MessageBox.Show(this, $"Не удалось сохранить:\n{err}",
                    "Пароли", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ReloadEntries();
        }

        private void EditEntry()
        {
            var e = SelectedEntry;
            if (e == null) return;
            var (name, login, password, notes) = EntryEditor.Show(this, "Изменить запись", e.GroupPath,
                e.Name, e.Login, e.Password, e.Notes);
            if (name == null) return;
            var err = KeePassVault.SaveLogin(e.GroupPath, name, login ?? "", password ?? "", notes);
            if (err != null)
            {
                MessageBox.Show(this, $"Не удалось сохранить:\n{err}",
                    "Пароли", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ReloadEntries();
        }

        private void DeleteEntry()
        {
            var e = SelectedEntry;
            if (e == null) return;
            var confirm = MessageBox.Show(this,
                $"Удалить запись «{e.Name}» из группы «{e.GroupPath}»?",
                "Пароли", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
            KeePassVault.DeleteEntry(e.GroupPath, e.Name);
            ReloadEntries();
        }

        // v3.7.1 — clone current entry into the same group. Opens the editor
        // pre-filled with " (копия)" suffix so the user can rename before save.
        // (KeePass entry names must be unique within a group; SaveLogin uses
        // name as the lookup key and would overwrite the source otherwise.)
        private void DuplicateEntry()
        {
            var e = SelectedEntry;
            if (e == null) return;
            var newName = e.Name + " (копия)";
            var (name, login, password, notes) = EntryEditor.Show(this, "Дублировать запись",
                e.GroupPath, newName, e.Login, e.Password, e.Notes);
            if (name == null) return;
            var err = KeePassVault.SaveLogin(e.GroupPath, name, login ?? "", password ?? "", notes);
            if (err != null)
            {
                MessageBox.Show(this, $"Не удалось сохранить:\n{err}",
                    "Пароли", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ReloadEntries();
        }

        private void CopyPassword()
        {
            var e = SelectedEntry;
            if (e == null) return;
            try
            {
                Clipboard.SetText(e.Password);
                _status.Text = $"Пароль «{e.Name}» скопирован в буфер обмена";
            }
            catch (Exception ex)
            {
                Logger.Warn($"KeePassVaultForm: clipboard failed: {ex.Message}");
            }
        }

        private void LockVault()
        {
            KeePassVault.Lock();
            Close();
        }

        // === Column-width persistence (mirrors OpenFilesForm) ===
        // v3.7.1: grid is now in None mode (was Fill), so this is just a
        // straight Width restore. The old FillWeight dance is gone — WinForms
        // no longer redistributes on form resize, user drags stick.
        private void LoadColumnWidths()
        {
            try
            {
                var path = AppPaths.ColumnWidthsPath;
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(ColumnWidthsFormKey, out var formEntry)) return;
                foreach (var prop in formEntry.EnumerateObject())
                {
                    if (!_grid.Columns.Contains(prop.Name)) continue;
                    int v = prop.Value.GetInt32();
                    if (v < 40) continue;
                    _grid.Columns[prop.Name]!.Width = v;
                }
                Logger.Info("KeePassVault LoadColumnWidths: restored widths");
            }
            catch (Exception ex)
            {
                Logger.Warn($"KeePassVault LoadColumnWidths failed: {ex.Message}");
            }
        }

        private void SaveColumnWidths()
        {
            try
            {
                if (IsDisposed) return;
                var path = AppPaths.ColumnWidthsPath;
                var dict = new Dictionary<string, Dictionary<string, int>>();
                if (File.Exists(path))
                {
                    try
                    {
                        dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(path))
                               ?? new Dictionary<string, Dictionary<string, int>>();
                    }
                    catch { dict = new Dictionary<string, Dictionary<string, int>>(); }
                }
                var widths = new Dictionary<string, int>();
                foreach (DataGridViewColumn c in _grid.Columns)
                    widths[c.Name] = c.Width;
                dict[ColumnWidthsFormKey] = widths;
                Directory.CreateDirectory(AppPaths.DataDir);
                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = false
                };
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(dict, opts));
            }
            catch (Exception ex)
            {
                Logger.Warn($"KeePassVault SaveColumnWidths failed: {ex.Message}");
            }
        }

        // v3.7.1 — SplitContainer distance (tree | grid). Same column_widths.json
        // file, separate namespace so the existing Dictionary<string,
        // Dictionary<string,int>> schema stays intact.
        //
        // MUST be called after the form's handle is created — SplitterDistance
        // setter needs _split.Width > 0 to clamp against. Wired to Form.Load,
        // not the ctor.
        private void LoadSplitDistance()
        {
            try
            {
                var path = AppPaths.ColumnWidthsPath;
                if (!File.Exists(path)) return;
                var json = File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty(SplitDistanceFormKey, out var splitEntry)) return;
                if (!splitEntry.TryGetProperty("distance", out var distProp)) return;
                int v = distProp.GetInt32();
                if (v < _split.Panel1MinSize + 10) return;
                // Clamp so a stale entry from a larger window doesn't throw.
                int max = _split.Width - _split.Panel2MinSize - _split.SplitterWidth;
                if (max < _split.Panel1MinSize + 10) return;
                v = Math.Min(v, max);
                _split.SplitterDistance = v;
                Logger.Info($"KeePassVault LoadSplitDistance: restored {v}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"KeePassVault LoadSplitDistance failed: {ex.Message}");
            }
        }

        private void SaveSplitDistance()
        {
            try
            {
                if (IsDisposed) return;
                var path = AppPaths.ColumnWidthsPath;
                var dict = new Dictionary<string, Dictionary<string, int>>();
                if (File.Exists(path))
                {
                    try
                    {
                        dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(path))
                               ?? new Dictionary<string, Dictionary<string, int>>();
                    }
                    catch { dict = new Dictionary<string, Dictionary<string, int>>(); }
                }
                dict[SplitDistanceFormKey] = new Dictionary<string, int> { { "distance", _split.SplitterDistance } };
                Directory.CreateDirectory(AppPaths.DataDir);
                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = false
                };
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(dict, opts));
            }
            catch (Exception ex)
            {
                Logger.Warn($"KeePassVault SaveSplitDistance failed: {ex.Message}");
            }
        }

        // Small modal editor for a single entry (name / login / password /
        // notes). Returns (Name, Login, Password, Notes) or (null, …) on
        // cancel.
        private static class EntryEditor
        {
            public static (string? Name, string? Login, string? Password, string? Notes) Show(
                IWin32Window owner, string title, string group, string name,
                string login, string password, string notes)
            {
                using var dlg = new Form
                {
                    Text = title,
                    Size = new Size(500, 530),
                    StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    BackColor = UiTheme.DialogBackground,
                    AutoScroll = false
                };
                dlg.KeyPreview = true;
                dlg.KeyDown += (_, ke) =>
                {
                    if (ke.KeyCode == Keys.Escape) { dlg.DialogResult = DialogResult.Cancel; ke.Handled = true; }
                };

                // ——— 1. Hero ———
                var hero = UiModal.Hero(title, "Запись хранилища KeePass", "key");

                // ——— 2. Card with fields ———
                var card = UiModal.Card(14, 14, 456, 320, "Данные записи", "key");
                var lName = UiModal.FieldLabel("Имя", 16, 46);
                var wName = UiModal.Field(new TextBox { Text = name }, 424);
                wName.Location = new Point(16, 62);
                var lLogin = UiModal.FieldLabel("Логин", 16, 100);
                var wLogin = UiModal.Field(new TextBox { Text = login }, 424);
                wLogin.Location = new Point(16, 116);
                var lPass = UiModal.FieldLabel("Пароль", 16, 154);
                var tbPass = new TextBox { Text = password };
                tbPass.UseSystemPasswordChar = true;
                var wPass = UiModal.Field(tbPass, 424);
                wPass.Location = new Point(16, 170);
                var lNotes = UiModal.FieldLabel("Заметки", 16, 208);
                var tbNotes = new Controls.ScrollHiddenTextBox
                {
                    Text = notes,
                    Multiline = true,
                    ScrollBars = ScrollBars.Vertical,
                    Location = new Point(16, 224),
                    Size = new Size(424, 56),
                    Font = new Font("Segoe UI", 9F),
                    BackColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle,
                    ForeColor = UiTheme.Foreground
                };
                var hint = new Label
                {
                    Text = $"Группа: {group}",
                    Location = new Point(16, 286),
                    AutoSize = false,
                    Size = new Size(424, 20),
                    Font = new Font("Segoe UI", 8.5F),
                    ForeColor = UiTheme.MutedForeground,
                    BackColor = Color.Transparent
                };
                card.Controls.AddRange(new Control[] { lName, wName, lLogin, wLogin, lPass, wPass, lNotes, tbNotes, hint });
                // v4.4.0 — своя тонкая полоса вместо системной.
                ShadowScroll.AttachTextBox(tbNotes);
                // v3.9.3Y — UiModal.Card гладкая сама, Region давал лесенку.
                try { card.Region?.Dispose(); card.Region = null; } catch { }

                // ——— 3. Footer ———
                var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
                footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
                var okBtn = UiButtons.MakeCompactFilled("OK", 100);
                okBtn.Location = new Point(12, 15);
                okBtn.Click += (_, _) => Submit();
                var cancelBtn = UiButtons.MakeCompactFilled("Отмена", 100,
                    bgOverride: UiTheme.MutedForeground);
                cancelBtn.Location = new Point(footer.Width - 112, 15);
                cancelBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                cancelBtn.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
                footer.Controls.Add(okBtn);
                footer.Controls.Add(cancelBtn);

                var hiddenOk = new Button { Visible = true, TabStop = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
                var hiddenCancel = new Button { DialogResult = DialogResult.Cancel, Visible = true, TabStop = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
                footer.Controls.Add(hiddenOk);
                footer.Controls.Add(hiddenCancel);
                dlg.AcceptButton = hiddenOk;
                dlg.CancelButton = hiddenCancel;

                string? rName = null, rLogin = null, rPassword = null, rNotes = null;
                void Submit()
                {
                    var txtName = ((TextBox)wName.Controls[0]).Text;
                    if (string.IsNullOrWhiteSpace(txtName))
                    {
                        MessageBox.Show(dlg, "Введите имя записи.",
                            "Пароли", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        ((TextBox)wName.Controls[0]).Focus();
                        return;
                    }
                    rName = txtName.Trim();
                    rLogin = ((TextBox)wLogin.Controls[0]).Text.Trim();
                    rPassword = ((TextBox)wPass.Controls[0]).Text;
                    rNotes = tbNotes.Text.Trim();
                    dlg.DialogResult = DialogResult.OK;
                }

                okBtn.Click += (_, _) => Submit();
                hiddenOk.Click += (_, _) => Submit();
                hiddenCancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;

                // ——— assemble ———
                var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.DialogBackground };
                content.Controls.Add(card);
                // v4.4.0 — своя тонкая полоса вместо системной.
                ShadowScroll.AttachPanel(content);
                dlg.Controls.Add(content);
                dlg.Controls.Add(hero);
                dlg.Controls.Add(footer);

                var dpiScale = UiModal.ApplyDpiScale(dlg);
                if (dpiScale > 1.001f)
                    dlg.ClientSize = new Size((int)Math.Round(dlg.ClientSize.Width * dpiScale), (int)Math.Round(dlg.ClientSize.Height * dpiScale));

                dlg.Shown += (_, _) =>
                {
                    cancelBtn.Location = new Point(footer.Width - 112, 15);
                    ((TextBox)wName.Controls[0]).Focus();
                    try { dlg.BeginInvoke(new Action(() => { try { dlg.Refresh(); } catch { } })); } catch { }
                };

                return UiModal.ShowDimmed(owner, dlg) == DialogResult.OK
                    ? (rName, rLogin, rPassword, rNotes)
                    : (null, null, null, null);
            }
        }
    }
}
