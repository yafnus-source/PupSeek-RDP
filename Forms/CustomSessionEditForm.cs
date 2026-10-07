using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Models;

namespace ShadowRDP.Forms
{
    // v2.1.83 — Dialog for adding / renaming a custom (non-RDP) session.
    // Used by the "+" button in RDP Manager header.
    //
    // Fields:
    //   Name    (required, free text — shows in sidebar)
    //   Type    (dropdown: SSH / Web, + RDP for new sessions)
    //   Target  (label adapts: "Хост:порт" for SSH, "URL" for Web,
//            "Хост/IP" for RDP; + "Имя компьютера" row in RDP mode)
    //   Group   (optional — datalist of existing group names)
    //   Notes   (optional — internal comment, not shown in sidebar)
    //
    // v4.9.26 — Тип RDP (только для новых): создаёт настоящую RDP-сессию
    // через RdpManagerForm.AddSessionFromBook (reuse-or-create + активация),
    // в custom-модель не попадает. Edit() в этом случае возвращает null
    // (вызывающие уже трактуют null как отмену).
    // v4.9.34 — RDP-форма: Название, Имя компьютера, Хост/IP, Группа,
    // Заметки → запись СРАЗУ в сессиях нужной группы (AddRdpSessionFull).
    // Corporate style: UiModal hero + card + footer (как настройки дашборда).
    //
    // Returns the edited CustomSession on OK, or null on Cancel.
    public class CustomSessionEditForm : Form
    {
        private readonly TextBox _nameBox;
        private readonly Panel _nameWrap;
        private readonly ComboBox _typeBox;
        private readonly Panel _typeWrap;
        private readonly TextBox _targetBox;
        private readonly Label _targetLabel;
        private readonly Panel _targetWrap;
        // v4.9.34 — строка «Имя компьютера» (только RDP).
        private readonly Label _computerLabel;
        private readonly TextBox _computerBox;
        private readonly Panel _computerWrap;
        // v4.9.34 — подписи/карточка/футер полями: RDP-режим сдвигает ряды.
        private readonly Label _lblType;
        private readonly Label _lblGroup;
        private readonly Label _lblNotes;
        private readonly Panel _card;
        private readonly Panel _footer;
        private readonly Label _okBtn;
        private readonly Label _cancelBtn;
        // v2.2.6: was TextBox — now ComboBox with dropdown of existing groups.
        private readonly ComboBox _groupBox;
        private readonly Panel _groupWrap;
        private readonly System.Collections.Generic.IEnumerable<string> _existingGroups;
        private readonly TextBox _notesBox;
        private readonly Panel _notesWrap;
        private readonly CustomSession _session;
        private readonly bool _isNew;
        // v4.9.34 — данные RDP для создания после закрытия диалога.
        private (string DisplayName, string Computer, string Ip, string Group, string Notes)? _rdp;

        // Pre-fill with existing groups so the user can pick from a dropdown
        // instead of typing the same name again.
        public static CustomSession? Edit(IWin32Window owner, CustomSession? existing = null, System.Collections.Generic.IEnumerable<string>? knownGroups = null)
        {
            using var dlg = new CustomSessionEditForm(existing, knownGroups);
            var res = ShadowRDP.Helpers.UiModal.ShowDimmed(owner, dlg);
            // v4.9.26 — RDP создаётся после закрытия диалога (модальные
            // промпты RDP поверх открытого диалога дали бы кашу z-order).
            if (dlg._rdp != null)
            {
                try
                {
                    var r = dlg._rdp.Value;
                    RdpManagerForm.Instance.AddRdpSessionFull(r.Computer, r.Ip, r.Group, r.DisplayName, r.Notes);
                }
                catch { }
                return null;
            }
            return res == DialogResult.OK ? dlg._session : null;
        }

        // v4.9.34 — RDP-форма: Название, Имя компьютера, Хост/IP, Группа,
// Заметки → запись СРАЗУ в сессиях нужной группы (AddRdpSessionFull).
        // Full FTP-стиль: hero + карточка С шапкой + UiModal.Field +
        // dock-футер 52px (как CalendarImportForm/FindUserDialog).
        private CustomSessionEditForm(CustomSession? existing, System.Collections.Generic.IEnumerable<string>? existingGroups = null)
        {
            _session = existing ?? new CustomSession();
            _isNew = existing == null;
            _existingGroups = existingGroups ?? System.Array.Empty<string>();

            Text = _isNew ? "Новая сессия" : "Изменить сессию";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;

            // === Hero ===
            var hero = Helpers.UiModal.Hero(
                _isNew ? "Новая сессия" : "Изменить сессию",
                _isNew ? "SSH, Web-ссылка или RDP-подключение в сайдбар" : "Параметры сессии в сайдбаре",
                "add");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            // === Footer (dock — всегда у нижнего края) ===
            _footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Helpers.UiTheme.DialogCard };
            _footer.Paint += (s, e) => { using var pen = new Pen(Helpers.UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0); };
            _okBtn = Helpers.UiButtons.MakeCompactFilled("OK", 100);
            _footer.Controls.Add(_okBtn);
            _cancelBtn = Helpers.UiButtons.MakeCompactFilled("Отмена", 100,
                bgOverride: ShadowRDP.Helpers.UiTheme.MutedForeground);
            _footer.Controls.Add(_cancelBtn);
            _footer.Resize += (s, e) =>
            {
                try
                {
                    _okBtn.Location = new Point(_footer.ClientSize.Width - 216, 15);
                    _cancelBtn.Location = new Point(_footer.ClientSize.Width - 108, 15);
                }
                catch { }
            };
            Controls.Add(_footer);

            // === Card с шапкой ===
            _card = Helpers.UiModal.Card(12, 90, 416, 304, "Сессия", "add");
            Controls.Add(_card);
            var card = _card;

            // Ряды: подпись 7.5pt + UiModal.Field. В карточке с шапкой
            // контент от (16,44).
            var lblName = Helpers.UiModal.FieldLabel("Название", 16, 48);
            card.Controls.Add(lblName);
            _nameBox = new TextBox { Text = _session.Name };
            _nameWrap = Helpers.UiModal.Field(_nameBox, 384);
            _nameWrap.Location = new Point(16, 64);
            card.Controls.Add(_nameWrap);

            // v4.9.34 — строка «Имя компьютера»: только RDP-режим.
            // Стоит между Названием и Типом; остальные ряды сдвигаются.
            _computerLabel = Helpers.UiModal.FieldLabel("Имя компьютера", 16, 100);
            _computerLabel.Visible = false;
            card.Controls.Add(_computerLabel);
            _computerBox = new TextBox();
            _computerWrap = Helpers.UiModal.Field(_computerBox, 384);
            _computerWrap.Location = new Point(16, 116);
            _computerWrap.Visible = false;
            card.Controls.Add(_computerWrap);

            _lblType = Helpers.UiModal.FieldLabel("Тип", 16, 100);
            card.Controls.Add(_lblType);
            _typeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            _typeWrap = Helpers.UiModal.Field(_typeBox, 384);
            _typeWrap.Location = new Point(16, 116);
            // v4.9.26 — RDP только для новых (переименование SSH/Web в RDP
            // не имеет смысла: RDP-сессии живут в своём списке).
            _typeBox.Items.AddRange(_isNew ? new object[] { "SSH", "Web", "RDP" } : new object[] { "SSH", "Web" });
            _typeBox.SelectedIndex = _session.Kind == SessionKind.Web ? 1 : 0;
            _typeBox.SelectedIndexChanged += (_, _) => UpdateTargetLabel();
            card.Controls.Add(_typeWrap);

            _targetLabel = Helpers.UiModal.FieldLabel("Хост:порт", 16, 148);
            card.Controls.Add(_targetLabel);
            _targetBox = new TextBox { Text = _session.Target };
            _targetWrap = Helpers.UiModal.Field(_targetBox, 384);
            _targetWrap.Location = new Point(16, 164);
            card.Controls.Add(_targetWrap);
            UpdateTargetLabel();

            _lblGroup = Helpers.UiModal.FieldLabel("Группа", 16, 196);
            card.Controls.Add(_lblGroup);
            // v2.2.6: Группа was a plain TextBox + AutoComplete. User
            // wanted explicit dropdown of existing groups. Now it's a
            // ComboBox with DropDown style (editable but with list).
            _groupBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
            _groupWrap = Helpers.UiModal.Field(_groupBox, 384);
            _groupWrap.Location = new Point(16, 212);
            _groupBox.Items.AddRange(_existingGroups.Select(g => (object)g).ToArray());
            _groupBox.Text = _session.Group ?? "";
            card.Controls.Add(_groupWrap);

            _lblNotes = Helpers.UiModal.FieldLabel("Заметки", 16, 244);
            card.Controls.Add(_lblNotes);
            _notesBox = new TextBox { Text = _session.Notes ?? "" };
            _notesWrap = Helpers.UiModal.Field(_notesBox, 384);
            _notesWrap.Location = new Point(16, 260);
            card.Controls.Add(_notesWrap);

            ClientSize = new Size(440, 454);

            // Hidden Buttons for Enter/Esc routing (Label doesn't implement
            // IButtonControl — same trick as RdpPasswordPrompt).
            var hiddenOk = new Button { Visible = true, TabStop = false, Location = new Point(-100, -100), Size = new Size(1, 1) };
            var hiddenCancel = new Button { Visible = true, TabStop = false, Location = new Point(-200, -200), Size = new Size(1, 1), DialogResult = DialogResult.Cancel };
            Controls.Add(hiddenOk);
            Controls.Add(hiddenCancel);
            AcceptButton = hiddenOk;
            CancelButton = hiddenCancel;

            void Submit()
            {
                var name = _nameBox.Text.Trim();
                var target = _targetBox.Text.Trim();
                bool isRdp = _isNew && _typeBox.SelectedIndex == 2;
                // v4.9.34 — RDP: Название (необязательно), Имя компьютера
                // (обязательно), Хост/IP, Группа, Заметки → запись СРАЗУ
                // в сессиях нужной группы (создаётся после закрытия).
                if (isRdp)
                {
                    var computer = _computerBox.Text.Trim();
                    if (string.IsNullOrEmpty(computer))
                    {
                        MessageBox.Show(this, "Укажите имя компьютера.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _computerBox.Focus();
                        return;
                    }
                    _rdp = (name, computer, target,
                        string.IsNullOrWhiteSpace(_groupBox.Text) ? "" : _groupBox.Text.Trim(),
                        _notesBox.Text.Trim());
                    DialogResult = DialogResult.Cancel;
                    Close();
                    return;
                }
                if (string.IsNullOrEmpty(name))
                {
                    MessageBox.Show(this, "Название не может быть пустым.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _nameBox.Focus();
                    return;
                }
                if (string.IsNullOrEmpty(target))
                {
                    MessageBox.Show(this, "Target не может быть пустым.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _targetBox.Focus();
                    return;
                }
                _session.Name = name;
                _session.Kind = _typeBox.SelectedIndex == 1 ? SessionKind.Web : SessionKind.Ssh;
                _session.Target = target;
                _session.Group = string.IsNullOrWhiteSpace(_groupBox.Text) ? null : _groupBox.Text.Trim();
                _session.Notes = string.IsNullOrWhiteSpace(_notesBox.Text) ? null : _notesBox.Text.Trim();
                DialogResult = DialogResult.OK;
            }

            _okBtn.Click += (_, _) => Submit();
            hiddenOk.Click += (_, _) => Submit();
            _cancelBtn.Click += (_, _) => DialogResult = DialogResult.Cancel;

            Shown += (_, _) => _nameBox.Focus();
        }

        // v4.9.34 — RDP-режим: строка «Имя компьютера» после Названия,
        // остальные ряды -48px вниз, карточка ниже (футер dock — сам).
        private void LayoutRdpMode(bool rdp)
        {
            _computerLabel.Visible = rdp;
            _computerWrap.Visible = rdp;
            int d = rdp ? 48 : 0;
            _lblType.Location = new Point(16, 100 + d);
            _typeWrap.Location = new Point(16, 116 + d);
            _targetLabel.Location = new Point(16, 148 + d);
            _targetWrap.Location = new Point(16, 164 + d);
            // v4.9.35 — первый UpdateTargetLabel() идёт из конструктора
            // ДО создания нижних рядов: дальше только если всё есть.
            if (_lblGroup == null || _lblNotes == null || _card == null)
                return;
            _lblGroup.Location = new Point(16, 196 + d);
            _groupWrap.Location = new Point(16, 212 + d);
            _lblNotes.Location = new Point(16, 244 + d);
            _notesWrap.Location = new Point(16, 260 + d);
            _card.Size = new Size(416, 304 + d);
            ClientSize = new Size(440, 454 + d);
        }

        private void UpdateTargetLabel()
        {
            // v4.9.26 — RDP (только новые): индекс 2.
            bool isRdp = _isNew && _typeBox.SelectedIndex == 2;
            _targetLabel.Text = isRdp ? "Хост/IP:" : _typeBox.SelectedIndex == 1 ? "URL:" : "Хост:порт:";
            LayoutRdpMode(isRdp);
        }
    }
}
