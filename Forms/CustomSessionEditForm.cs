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
    //   Type    (dropdown: SSH / Web)
    //   Target  (label adapts: "Хост:порт" for SSH, "URL" for Web)
    //   Group   (optional — datalist of existing group names)
    //   Notes   (optional — internal comment, not shown in sidebar)
    //
    // Returns the edited CustomSession on OK, or null on Cancel.
    public class CustomSessionEditForm : Form
    {
        private readonly TextBox _nameBox;
        private readonly ComboBox _typeBox;
        private readonly TextBox _targetBox;
        private readonly Label _targetLabel;
        // v2.2.6: was TextBox — now ComboBox with dropdown of existing groups.
        private readonly ComboBox _groupBox;
        private readonly System.Collections.Generic.IEnumerable<string> _existingGroups;
        private readonly TextBox _notesBox;
        private readonly CustomSession _session;

        // Pre-fill with existing groups so the user can pick from a dropdown
        // instead of typing the same name again.
        public static CustomSession? Edit(IWin32Window owner, CustomSession? existing = null, System.Collections.Generic.IEnumerable<string>? knownGroups = null)
        {
            using var dlg = new CustomSessionEditForm(existing, knownGroups);
            return dlg.ShowDialog(owner) == DialogResult.OK ? dlg._session : null;
        }

        private CustomSessionEditForm(CustomSession? existing, System.Collections.Generic.IEnumerable<string>? existingGroups = null)
        {
            _session = existing ?? new CustomSession();
            _existingGroups = existingGroups ?? System.Array.Empty<string>();

            Text = existing == null ? "Новая сессия" : "Изменить сессию";
            ClientSize = new Size(440, 260);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.Background;

            // === Name ===
            var lblName = new Label
            {
                Text = "Название:",
                Location = new Point(16, 16),
                Size = new Size(120, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            Controls.Add(lblName);
            _nameBox = new TextBox
            {
                Location = new Point(140, 14),
                Size = new Size(284, 26),
                Font = new Font("Segoe UI", 10F),
                Text = _session.Name
            };
            Controls.Add(_nameBox);

            // === Type ===
            var lblType = new Label
            {
                Text = "Тип:",
                Location = new Point(16, 50),
                Size = new Size(120, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            Controls.Add(lblType);
            _typeBox = new ComboBox
            {
                Location = new Point(140, 48),
                Size = new Size(284, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10F)
            };
            _typeBox.Items.AddRange(new object[] { "SSH", "Web" });
            _typeBox.SelectedIndex = _session.Kind == SessionKind.Web ? 1 : 0;
            _typeBox.SelectedIndexChanged += (_, _) => UpdateTargetLabel();
            Controls.Add(_typeBox);

            // === Target ===
            _targetLabel = new Label
            {
                Location = new Point(16, 84),
                Size = new Size(120, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            Controls.Add(_targetLabel);
            _targetBox = new TextBox
            {
                Location = new Point(140, 82),
                Size = new Size(284, 26),
                Font = new Font("Segoe UI", 10F),
                Text = _session.Target
            };
            Controls.Add(_targetBox);
            UpdateTargetLabel();

            // === Group ===
            var lblGroup = new Label
            {
                Text = "Группа:",
                Location = new Point(16, 118),
                Size = new Size(120, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            Controls.Add(lblGroup);
            // v2.2.6: Группа was a plain TextBox + AutoComplete. User
            // wanted explicit dropdown of existing groups. Now it's a
            // ComboBox with DropDown style (editable but with list).
            _groupBox = new ComboBox
            {
                Location = new Point(140, 116),
                Size = new Size(284, 26),
                DropDownStyle = ComboBoxStyle.DropDown,
                Font = new Font("Segoe UI", 10F)
            };
            _groupBox.Items.AddRange(_existingGroups.Select(g => (object)g).ToArray());
            _groupBox.Text = _session.Group ?? "";
            Controls.Add(_groupBox);

            // === Notes ===
            var lblNotes = new Label
            {
                Text = "Заметки:",
                Location = new Point(16, 152),
                Size = new Size(120, 22),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Helpers.UiTheme.Foreground
            };
            Controls.Add(lblNotes);
            _notesBox = new TextBox
            {
                Location = new Point(140, 150),
                Size = new Size(284, 26),
                Font = new Font("Segoe UI", 10F),
                Text = _session.Notes ?? ""
            };
            Controls.Add(_notesBox);

            // === Buttons (UiButtons → Label, hidden Button for Accept/Cancel) ===
            var okBtn = Helpers.UiButtons.MakeFilled("OK", null, ShadowRDP.Helpers.UiTheme.HeaderBar, width: 96, radius: 1);
            okBtn.Location = new Point(232, 196);
            Controls.Add(okBtn);

            var cancelBtn = Helpers.UiButtons.MakeFlat("Отмена");
            cancelBtn.Location = new Point(344, 196);
            Controls.Add(cancelBtn);

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

            okBtn.Click += (_, _) => Submit();
            hiddenOk.Click += (_, _) => Submit();
            cancelBtn.Click += (_, _) => DialogResult = DialogResult.Cancel;

            Shown += (_, _) => _nameBox.Focus();
        }

        private void UpdateTargetLabel()
        {
            _targetLabel.Text = _typeBox.SelectedIndex == 1 ? "URL:" : "Хост:порт:";
        }
    }
}
