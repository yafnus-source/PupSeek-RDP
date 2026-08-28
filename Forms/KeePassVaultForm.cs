using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.7.0 — KeePass vault manager: browse groups, view/save/delete
    // entries. Opened from the tray menu ("Пароли…"). If the vault isn't
    // open yet, the unlock dialog appears first.
    //
    // Redesigned v3.7.0 to match the report dialogs (OpenFiles etc.):
    // blue header bar (UiHeader), "+ Сессия"/"+ В книгу"-style compact
    // filled toolbar buttons, borderless themed DataGridView. Column widths
    // persist in column_widths.json (key "keepassVault") and column order
    // via GridColumnOrder, window size via WindowStateStore.
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

        private readonly TreeView _tree;
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
            BackColor = UiTheme.Background;
            FormBorderStyle = FormBorderStyle.Sizable;

            // === Blue header bar (same as Open Files / Computer Info) ===
            var header = UiHeader.Create("Пароли", "хранилище KeePass (.kdbx)", "shield", ShadowRDP.Helpers.UiTheme.HeaderBar);
            header.Dock = DockStyle.Top;

            // === Toolbar row: compact filled buttons ("+ Сессия" / "+ В книгу" look) ===
            var toolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = UiTheme.Background
            };
            var btnFont = new Font("Segoe UI Semibold", 9F);
            int w(string text) => TextRenderer.MeasureText(text, btnFont).Width + 28;

            var btnAdd = UiButtons.MakeCompactFilled("Новая запись", w("Новая запись"));
            btnAdd.Location = new Point(16, 11);
            var btnEdit = UiButtons.MakeCompactFilled("Изменить", w("Изменить"));
            btnEdit.Location = new Point(16 + btnAdd.Width + 8, 11);
            var btnDelete = UiButtons.MakeCompactFilled("Удалить", w("Удалить"), danger: true);
            btnDelete.Location = new Point(btnEdit.Left + btnEdit.Width + 8, 11);
            var btnCopy = UiButtons.MakeCompactFilled("Копировать пароль", w("Копировать пароль"));
            btnCopy.Location = new Point(btnDelete.Left + btnDelete.Width + 8, 11);

            var btnLock = UiButtons.MakeCompactFilled("Заблокировать", w("Заблокировать"));
            btnLock.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            var btnClose = UiButtons.MakeCompactFilled("Закрыть", w("Закрыть"));
            btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // Right-cluster anchored to the toolbar's right edge; positioned
            // on Resize so the pair hugs the right edge together.
            // v3.7.1: Top=11 to vertically align with btnAdd/btnEdit/etc.
            // (Anchor=Top|Right left Top=0; other buttons use Location.Y=11.)
            toolbar.Resize += (s, e) =>
            {
                btnLock.Left = toolbar.ClientSize.Width - 16 - btnClose.Width - 8 - btnLock.Width;
                btnLock.Top = 11;
                btnClose.Left = toolbar.ClientSize.Width - 16 - btnClose.Width;
                btnClose.Top = 11;
            };

            _btnEdit = btnEdit;
            _btnDelete = btnDelete;
            _btnCopy = btnCopy;

            toolbar.Controls.AddRange(new Control[] { btnAdd, btnEdit, btnDelete, btnCopy, btnLock, btnClose });

            btnAdd.Click += (_, _) => AddEntry();
            btnEdit.Click += (_, _) => EditEntry();
            btnDelete.Click += (_, _) => DeleteEntry();
            btnCopy.Click += (_, _) => CopyPassword();
            btnLock.Click += (_, _) => LockVault();
            btnClose.Click += (_, _) => Close();

            // === Split: group tree | entries grid ===
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 260,
                SplitterWidth = 6,
                BackColor = UiTheme.Background
            };

            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                HideSelection = false,
                // v3.7.1 — white background for the groups tree (was Card).
                // User request: левая колонка с деревом групп PupSeekRDP
                // должна быть на белом фоне.
                BackColor = UiTheme.Background,
                ForeColor = Color.FromArgb(0x1E, 0x1E, 0x1E),
                Font = new Font("Segoe UI", 9F),
                ItemHeight = 22
            };
            _split.Panel1.Controls.Add(_tree);

            // === Entries grid (borderless, themed — same as Open Files) ===
            // v3.7.1: AutoSizeColumnsMode switched from Fill to None. In Fill
            // mode the grid redistributes column widths on every form resize
            // using FillWeight as the proportion — user drags were captured
            // as Width but then overridden by the next Fill-layout pass, so
            // column widths never actually persisted. None mode keeps Width
            // stable; columns the user doesn't touch stay at their defaults.
            _grid = new Controls.DoubleBufferedDataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.None,
                GridColor = Color.White,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                DefaultCellStyle = {
                    BackColor = Color.White,
                    SelectionBackColor = UiTheme.AccentSoft,
                    SelectionForeColor = UiTheme.Foreground,
                    // v3.7.1: row height reduced 28 → 23 (−5px per user
                    // request). Padding stays (5,4,5,4) — 15px text + 8px
                    // vertical padding = 23, content still fits.
                    Padding = new Padding(5, 4, 5, 4)
                },
                ColumnHeadersDefaultCellStyle = {
                    BackColor = UiTheme.Muted,
                    ForeColor = UiTheme.MutedForeground,
                    Font = new Font("Segoe UI Semibold", 9F),
                    Padding = new Padding(5, 0, 5, 0)
                },
                RowTemplate = { Height = 23 }
            };
            _grid.EnableHeadersVisualStyles = false;
            _grid.AdvancedCellBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Top = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Bottom = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Left = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedCellBorderStyle.Right = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            _grid.AdvancedColumnHeadersBorderStyle.Bottom = DataGridViewAdvancedCellBorderStyle.None;
            // v3.7.1 — explicit Width instead of FillWeight (None mode).
            // Default widths chosen so Name + Login together comfortably
            // fill the right pane of a 880px-wide form (split.Panel2 ≈ 614px
            // after the 260px tree + 6px splitter); user-resizes persist via
            // SaveColumnWidths / LoadColumnWidths.
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Имя", Width = 400, MinimumWidth = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Login", HeaderText = "Логин", Width = 200, MinimumWidth = 90 });
            foreach (DataGridViewColumn c in _grid.Columns) c.DividerWidth = 0;
            _grid.RowTemplate.DividerHeight = 0;
            UiGridStyle.PaintHeaders(_grid, 6);
            _split.Panel2.Controls.Add(_grid);

            // === Status bar ===
            _status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                ForeColor = UiTheme.MutedForeground,
                Font = new Font("Segoe UI", 8.5F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 0, 0)
            };

            // Dock order matters: Fill first, then Top, then Top, then Bottom —
            // the last added docks first, so header takes the top edge, toolbar
            // sits under it, split fills the middle, status hugs the bottom.
            Controls.Add(_split);
            Controls.Add(toolbar);
            Controls.Add(header);
            Controls.Add(_status);

            _tree.AfterSelect += (_, _) => ReloadEntries();
            _grid.SelectionChanged += (_, _) =>
            {
                var has = _grid.SelectedRows.Count > 0;
                _btnEdit.Enabled = has;
                _btnDelete.Enabled = has;
                _btnCopy.Enabled = has;
            };
            _grid.CellMouseDoubleClick += (_, _) => CopyPassword();

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
            GridColumnOrder.Wire(_grid);
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
                    ClientSize = new Size(460, 260),
                    StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    Font = new Font("Segoe UI", 9F),
                    BackColor = UiTheme.Background
                };

                AddRow(dlg, "Имя:", 14, out var tbName);
                tbName.Text = name;
                AddRow(dlg, "Логин:", 50, out var tbLogin);
                tbLogin.Text = login;
                AddRow(dlg, "Пароль:", 86, out var tbPass);
                tbPass.Text = password;
                tbPass.UseSystemPasswordChar = true;

                var lblNotes = new Label
                {
                    Text = "Заметки:",
                    Location = new Point(16, 122),
                    Size = new Size(80, 22),
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = UiTheme.Foreground
                };
                dlg.Controls.Add(lblNotes);

                var tbNotes = new TextBox
                {
                    Location = new Point(100, 122),
                    Size = new Size(344, 60),
                    Multiline = true,
                    ScrollBars = ScrollBars.Vertical,
                    Font = new Font("Segoe UI", 9F)
                };
                tbNotes.Text = notes;
                dlg.Controls.Add(tbNotes);

                var hint = new Label
                {
                    Text = $"Группа: {group}",
                    Location = new Point(16, 190),
                    Size = new Size(428, 20),
                    ForeColor = UiTheme.MutedForeground,
                    Font = new Font("Segoe UI", 8.5F)
                };
                dlg.Controls.Add(hint);

                // v3.7.1 — compact filled buttons for consistency with the
                // toolbar and unlock dialog (was MakeFilled 36px tall).
                var okBtn = UiButtons.MakeCompactFilled("OK", 96);
                okBtn.Location = new Point(236, 218);
                dlg.Controls.Add(okBtn);

                var cancelBtn = UiButtons.MakeCompactFilled("Отмена", 96,
                    bgOverride: UiTheme.MutedForeground);
                cancelBtn.Location = new Point(344, 218);
                dlg.Controls.Add(cancelBtn);

                string? rName = null, rLogin = null, rPassword = null, rNotes = null;
                void Submit()
                {
                    if (string.IsNullOrWhiteSpace(tbName.Text))
                    {
                        MessageBox.Show(dlg, "Введите имя записи.",
                            "Пароли", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        tbName.Focus();
                        return;
                    }
                    rName = tbName.Text.Trim();
                    rLogin = tbLogin.Text.Trim();
                    rPassword = tbPass.Text;
                    rNotes = tbNotes.Text.Trim();
                    dlg.DialogResult = DialogResult.OK;
                }

                okBtn.Click += (_, _) => Submit();
                cancelBtn.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;

                var hiddenCancel = new Button
                {
                    DialogResult = DialogResult.Cancel,
                    Visible = true,
                    TabStop = false,
                    Location = new Point(-200, -200),
                    Size = new Size(1, 1)
                };
                hiddenCancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
                dlg.Controls.Add(hiddenCancel);
                dlg.CancelButton = hiddenCancel;

                var hiddenOk = new Button
                {
                    Visible = true,
                    TabStop = false,
                    Location = new Point(-300, -300),
                    Size = new Size(1, 1)
                };
                hiddenOk.Click += (_, _) => Submit();
                dlg.Controls.Add(hiddenOk);
                dlg.AcceptButton = hiddenOk;

                dlg.KeyPreview = true;
                dlg.KeyDown += (_, ke) =>
                {
                    if (ke.KeyCode == Keys.Enter)
                    {
                        Submit();
                        ke.Handled = true;
                        ke.SuppressKeyPress = true;
                    }
                    else if (ke.KeyCode == Keys.Escape)
                    {
                        dlg.DialogResult = DialogResult.Cancel;
                        ke.Handled = true;
                    }
                };

                dlg.Shown += (_, _) => tbName.Focus();

                return dlg.ShowDialog(owner) == DialogResult.OK
                    ? (rName, rLogin, rPassword, rNotes)
                    : (null, null, null, null);
            }

            private static void AddRow(Form dlg, string label, int y, out TextBox box)
            {
                dlg.Controls.Add(new Label
                {
                    Text = label,
                    Location = new Point(16, y),
                    Size = new Size(80, 22),
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = UiTheme.Foreground
                });
                box = new TextBox
                {
                    Location = new Point(100, y - 2),
                    Size = new Size(344, 26),
                    Font = new Font("Segoe UI", 10F)
                };
                dlg.Controls.Add(box);
            }
        }
    }
}
