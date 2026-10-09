using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Models;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v3.9.4 — управление SSH-туннелями: список + параметры + запуск.
    // Креды спрашиваются при каждом запуске (диалог SshCredsDialog),
    // в json не сохраняются. Статусы — из ForwardManager (StateChanged).
    public class PortForwardForm : Form
    {
        private readonly DataGridView _grid;
        private readonly TextBox _nameBox;
        private readonly ComboBox _sessionCombo;
        private readonly ComboBox _kindCombo;
        private readonly TextBox _bindPortBox;
        private readonly TextBox _targetHostBox;
        private readonly TextBox _targetPortBox;
        private readonly Label _runBtn;
        private readonly Label _saveBtn;
        private readonly Label _delBtn;
        private readonly Label _closeBtn;
        private List<CustomSession> _sshSessions = new();
        private string? _selectedId;
        private bool _busy;

        public static void Show(IWin32Window owner)
        {
            using var f = new PortForwardForm();
            Helpers.UiModal.ShowDimmed(owner, f);
        }

        public PortForwardForm()
        {
            Text = "Туннели SSH";
            StartPosition = FormStartPosition.CenterParent;
            // v3.9.5 — регулировка мышкой + запоминание размера; минимум
            // под ряд «Имя · Сессия SSH» и «Тип · Порт · Назначение».
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            MinimumSize = new Size(680, 480);
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;
            ClientSize = new Size(780, 560);

            var hero = Helpers.UiModal.Hero("Туннели SSH",
                "local · remote · dynamic (SOCKS5) — port forwarding", "exchange");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Helpers.UiTheme.DialogCard };
            footer.Paint += (s, e) =>
            {
                using var pen = new Pen(Helpers.UiTheme.Border, 1);
                e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            _runBtn = Helpers.UiButtons.MakeCompactFilled("Запустить", 130);
            _saveBtn = Helpers.UiButtons.MakeCompactFilled("Сохранить", 110);
            _delBtn = Helpers.UiButtons.MakeCompactFilled("Удалить", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            _closeBtn = Helpers.UiButtons.MakeCompactFilled("Закрыть", 100,
                bgOverride: Helpers.UiTheme.MutedForeground);
            footer.Controls.Add(_runBtn);
            footer.Controls.Add(_saveBtn);
            footer.Controls.Add(_delBtn);
            footer.Controls.Add(_closeBtn);
            footer.Resize += (s, e) =>
            {
                try
                {
                    int w = footer.ClientSize.Width;
                    _runBtn.Location = new Point(w - 496, 15);
                    _saveBtn.Location = new Point(w - 358, 15);
                    _delBtn.Location = new Point(w - 240, 15);
                    _closeBtn.Location = new Point(w - 132, 15);
                }
                catch { }
            };
            Controls.Add(footer);

            // v3.9.5 — список туннелей в стиле «Открытых файлов».
            var cardList = Helpers.UiModal.Card(12, 92, 756, 176, "", "", withHeader: false);
            try { cardList.Region?.Dispose(); cardList.Region = null; } catch { }
            Controls.Add(cardList);

            _grid = new DoubleBufferedDataGridView
            {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                MultiSelect = false
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Name", HeaderText = "Имя", FillWeight = 20, MinimumWidth = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Kind", HeaderText = "Тип", FillWeight = 12, MinimumWidth = 70 });
            var colBind = new DataGridViewTextBoxColumn
            { Name = "Bind", HeaderText = "Порт", FillWeight = 8, MinimumWidth = 50 };
            colBind.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            colBind.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.Columns.Add(colBind);
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Target", HeaderText = "Назначение", FillWeight = 31, MinimumWidth = 130 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Ssh", HeaderText = "SSH", FillWeight = 20, MinimumWidth = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "Status", HeaderText = "Статус", FillWeight = 9, MinimumWidth = 66 });
            Helpers.UiGridStyle.ApplyCardGrid(_grid, cardList);
            _grid.SelectionChanged += (_, _) => OnSelect();
            _grid.CellDoubleClick += (_, _) => _ = ToggleRunAsync();

            var card = Helpers.UiModal.Card(12, 280, 756, 176, "Параметры", "gear");

            card.Controls.Add(Helpers.UiModal.FieldLabel("Имя", 16, 48));
            _nameBox = new TextBox();
            var nameWrap = Helpers.UiModal.Field(_nameBox, 270);
            nameWrap.Location = new Point(16, 64);
            card.Controls.Add(nameWrap);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Сессия SSH", 306, 48));
            _sessionCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(306, 64),
                Size = new Size(300, 28),
                BackColor = Helpers.UiTheme.DialogCard,
                ForeColor = Helpers.UiTheme.Foreground
            };
            Helpers.UiCombo.Apply(_sessionCombo);
            card.Controls.Add(_sessionCombo);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Тип", 16, 108));
            _kindCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(16, 124),
                Size = new Size(170, 28),
                BackColor = Helpers.UiTheme.DialogCard,
                ForeColor = Helpers.UiTheme.Foreground
            };
            _kindCombo.Items.AddRange(new object[] { "локальный", "удалённый", "SOCKS5 (dynamic)" });
            _kindCombo.SelectedIndex = 0;
            Helpers.UiCombo.Apply(_kindCombo);
            card.Controls.Add(_kindCombo);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Порт", 200, 108));
            _bindPortBox = new TextBox();
            var bindWrap = Helpers.UiModal.Field(_bindPortBox, 80);
            bindWrap.Location = new Point(200, 124);
            card.Controls.Add(bindWrap);

            card.Controls.Add(Helpers.UiModal.FieldLabel("Хост назначения", 296, 108));
            _targetHostBox = new TextBox();
            var thWrap = Helpers.UiModal.Field(_targetHostBox, 240);
            thWrap.Location = new Point(296, 124);
            card.Controls.Add(thWrap);

            var tpLbl = Helpers.UiModal.FieldLabel("Порт назнач.", 550, 108);
            card.Controls.Add(tpLbl);
            _targetPortBox = new TextBox();
            var tpWrap = Helpers.UiModal.Field(_targetPortBox, 80);
            tpWrap.Location = new Point(550, 124);
            card.Controls.Add(tpWrap);
            Controls.Add(card);

            _kindCombo.SelectedIndexChanged += (_, _) =>
            {
                bool dynamic = _kindCombo.SelectedIndex == 2;
                _targetHostBox.Enabled = !dynamic;
                _targetPortBox.Enabled = !dynamic;
            };

            var hiddenClose = new Button
            {
                Visible = true, TabStop = false,
                Location = new Point(-200, -200), Size = new Size(1, 1),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(hiddenClose);
            CancelButton = hiddenClose;

            _runBtn.Click += async (_, _) => await ToggleRunAsync();
            _saveBtn.Click += (_, _) => Save();
            _delBtn.Click += (_, _) => Delete();
            _closeBtn.Click += (_, _) => Close();

            LoadSessions();
            ForwardManager.StateChanged += OnStateChanged;
            FormClosed += (_, _) => ForwardManager.StateChanged -= OnStateChanged;
            Render(null);

            // v3.9.5 — ресайз: карточки по ширине окна, «Параметры»
            // прижата к футеру, список тянется в остаток высоты.
            Resize += (_, _) => LayoutContent();
            LayoutContent();
            Services.WindowStateStore.Register(this, "portForward");

            void LayoutContent()
            {
                try
                {
                    int w = ClientSize.Width, h = ClientSize.Height;
                    int cw = Math.Max(400, w - 24);
                    cardList.Width = cw;
                    card.Width = cw;
                    // Параметры — над футером (штатный отступ 52px),
                    // список не ниже 80px (92 + 80 + зазор 12).
                    card.Top = Math.Max(184, h - 52 - 52 - 176);
                    cardList.Height = Math.Max(80, card.Top - 92 - 12);

                    int iw = card.ClientSize.Width;
                    _sessionCombo.Width = Math.Max(300, iw - 306 - 16);
                    // «Порт назнач.» прижат вправо, хост тянется в остаток.
                    int tpLeft = Math.Max(380, iw - 96);
                    tpLbl.Left = tpLeft;
                    tpWrap.Left = tpLeft;
                    thWrap.Width = Math.Max(160, tpLeft - 8 - 296);
                }
                catch { }
            }
        }

        private void OnStateChanged()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() => Render(_selectedId)));
            }
            catch { }
        }

        private void LoadSessions()
        {
            _sshSessions = CustomSessionStore.Load()
                .Where(s => s.Kind == SessionKind.Ssh)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _sessionCombo.Items.Clear();
            foreach (var s in _sshSessions)
            {
                var (host, _) = ForwardManager.ParseTarget(s.Target);
                _sessionCombo.Items.Add($"{s.Name} ({host})");
            }
        }

        private void Render(string? selectId)
        {
            var selected = selectId ?? _selectedId;
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var t in PortForwardStore.All())
            {
                var sess = _sshSessions.FirstOrDefault(s => s.Id == t.SessionId);
                var sshLabel = sess != null ? sess.Target : "—";
                var target = t.Kind == "dynamic" ? "SOCKS5" : $"{t.TargetHost}:{t.TargetPort}";
                var running = ForwardManager.IsRunning(t.Id);
                int ri = _grid.Rows.Add(t.Name, t.Kind, t.BindPort.ToString(),
                    target, sshLabel, running ? "запущен" : "стоп");
                var row = _grid.Rows[ri];
                row.Tag = t.Id;
                if (running) row.DefaultCellStyle.ForeColor = Color.FromArgb(56, 161, 105);
                if (selected != null && string.Equals(t.Id, selected, StringComparison.OrdinalIgnoreCase))
                    row.Selected = true;
            }
            _grid.ResumeLayout();
            UpdateRunButton();
        }

        private void UpdateRunButton()
        {
            var t = Current();
            _runBtn.Text = t != null && ForwardManager.IsRunning(t.Id) ? "Остановить" : "Запустить";
        }

        private PortForward? Current()
        {
            if (_grid.SelectedRows.Count == 0) return null;
            var id = _grid.SelectedRows[0].Tag as string;
            return id == null ? null : PortForwardStore.Find(id);
        }

        private void OnSelect()
        {
            var t = Current();
            if (t == null) return;
            _selectedId = t.Id;
            _nameBox.Text = t.Name;
            _kindCombo.SelectedIndex = t.Kind switch { "remote" => 1, "dynamic" => 2, _ => 0 };
            _bindPortBox.Text = t.BindPort.ToString();
            _targetHostBox.Text = t.TargetHost;
            _targetPortBox.Text = t.TargetPort > 0 ? t.TargetPort.ToString() : "";
            var idx = _sshSessions.FindIndex(s => s.Id == t.SessionId);
            _sessionCombo.SelectedIndex = idx;
            UpdateRunButton();
        }

        private void ClearFields()
        {
            _nameBox.Text = "";
            _sessionCombo.SelectedIndex = -1;
            _kindCombo.SelectedIndex = 0;
            _bindPortBox.Text = "";
            _targetHostBox.Text = "";
            _targetPortBox.Text = "";
            _selectedId = null;
            _grid.ClearSelection();
            UpdateRunButton();
        }

        private PortForward? BuildFromFields(out string? error)
        {
            error = null;
            var name = _nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) { error = "Укажите имя туннеля."; return null; }
            if (_sessionCombo.SelectedIndex < 0 || _sessionCombo.SelectedIndex >= _sshSessions.Count)
            {
                error = "Выберите SSH-сессию (создайте её в «Добавить сессию»).";
                return null;
            }
            if (!int.TryParse(_bindPortBox.Text.Trim(), out var bind) || bind < 1 || bind > 65535)
            {
                error = "Порт должен быть числом 1…65535.";
                return null;
            }
            var kind = _kindCombo.SelectedIndex switch { 1 => "remote", 2 => "dynamic", _ => "local" };
            var t = new PortForward
            {
                Id = _selectedId ?? Guid.NewGuid().ToString("N"),
                Name = name,
                SessionId = _sshSessions[_sessionCombo.SelectedIndex].Id,
                Kind = kind,
                BindPort = bind
            };
            if (kind != "dynamic")
            {
                t.TargetHost = _targetHostBox.Text.Trim();
                if (string.IsNullOrEmpty(t.TargetHost)) { error = "Укажите хост назначения."; return null; }
                if (!int.TryParse(_targetPortBox.Text.Trim(), out var tp) || tp < 1 || tp > 65535)
                {
                    error = "Порт назначения должен быть числом 1…65535.";
                    return null;
                }
                t.TargetPort = tp;
            }
            var invalid = ForwardManager.Validate(t);
            if (invalid != null) { error = invalid; return null; }
            return t;
        }

        private void Save()
        {
            var t = BuildFromFields(out var error);
            if (t == null)
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            PortForwardStore.Upsert(t);
            Render(t.Id);
        }

        private void Delete()
        {
            var t = Current() ?? (_selectedId != null ? PortForwardStore.Find(_selectedId) : null);
            if (t == null)
            {
                MessageBox.Show(this, "Выберите туннель для удаления.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (ForwardManager.IsRunning(t.Id)) ForwardManager.Stop(t.Id);
            PortForwardStore.Remove(t.Id);
            ClearFields();
            Render(null);
        }

        private async Task ToggleRunAsync()
        {
            if (_busy) return;
            var t = Current();
            if (t == null)
            {
                MessageBox.Show(this, "Выберите туннель.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (ForwardManager.IsRunning(t.Id))
            {
                ForwardManager.Stop(t.Id);
                Render(t.Id);
                return;
            }

            // Параметры могли правиться в полях — сохраняем свежими.
            t = BuildFromFields(out var error) ?? t;
            if (error != null && !string.IsNullOrEmpty(_nameBox.Text))
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var sess = _sshSessions.FirstOrDefault(s => s.Id == t.SessionId);
            if (sess == null)
            {
                MessageBox.Show(this, "Сессия SSH не найдена.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var (sshHost, sshPort) = ForwardManager.ParseTarget(sess.Target);
            if (string.IsNullOrEmpty(sshHost))
            {
                MessageBox.Show(this, "У сессии не задан хост.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var creds = Helpers.SshCredsDialog.Prompt(this, sshHost, sess.SshKeyPath);
            if (creds == null) return;

            _busy = true;
            _runBtn.Text = "Подключение…";
            try
            {
                await Task.Run(() => ForwardManager.Start(t, sshHost, sshPort,
                    creds.User, creds.Password, sess.SshKeyPath, this));
                Render(t.Id);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Не удалось запустить туннель:\r\n{ex.Message}", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Render(t.Id);
            }
            finally
            {
                _busy = false;
                UpdateRunButton();
            }
        }
    }
}
