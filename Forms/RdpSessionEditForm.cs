using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ShadowRDP.Forms
{
    // v4.9.29 — Диалог «Редактировать запись» для RDP-сессии сайдбара.
    // v4.9.34 — поля: Название, Имя компьютера (обязательно), Хост/IP,
    // Группа (выбор из существующих + свой текст), Заметки.
    // Corporate style: UiModal hero + card + footer.
    //
    // Возвращает (DisplayName, Computer, Ip, Group, Notes) на OK, null на Cancel.
    // Дубликаты имён проверяет вызывающий (ему виден список сессий).
    public class RdpSessionEditForm : Form
    {
        private readonly TextBox _nameBox;
        private readonly TextBox _computerBox;
        private readonly TextBox _ipBox;
        private readonly ComboBox _groupBox;
        private readonly TextBox _notesBox;

        public static (string DisplayName, string Computer, string Ip, string Group, string Notes)? Edit(
            IWin32Window owner, string displayName, string computer, string ip, string group, string notes,
            System.Collections.Generic.IEnumerable<string>? knownGroups = null)
        {
            using var dlg = new RdpSessionEditForm(displayName, computer, ip, group, notes, knownGroups);
            if (ShadowRDP.Helpers.UiModal.ShowDimmed(owner, dlg) != DialogResult.OK)
                return null;
            return (dlg._nameBox.Text.Trim(), dlg._computerBox.Text.Trim(), dlg._ipBox.Text.Trim(),
                string.IsNullOrWhiteSpace(dlg._groupBox.Text) ? "" : dlg._groupBox.Text.Trim(),
                dlg._notesBox.Text.Trim());
        }

        private RdpSessionEditForm(string displayName, string computer, string ip, string group, string notes, System.Collections.Generic.IEnumerable<string>? existingGroups = null)
        {
            Text = "Редактировать запись";
            ClientSize = new Size(440, 444);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;

            var hero = Helpers.UiModal.Hero("Редактировать запись", "Название, компьютер, IP, группа и заметки", "info");
            Controls.Add(hero);

            var card = Helpers.UiModal.Card(12, 90, 416, 310, "Сессия", "info", withHeader: false);
            Controls.Add(card);

            var lblName = Helpers.UiModal.FieldLabel("Название", 16, 14);
            card.Controls.Add(lblName);
            _nameBox = new TextBox
            {
                Location = new Point(16, 30),
                Size = new Size(384, 26),
                Font = new Font("Segoe UI", 10F),
                Text = displayName
            };
            card.Controls.Add(_nameBox);

            var lblComputer = Helpers.UiModal.FieldLabel("Имя компьютера", 16, 62);
            card.Controls.Add(lblComputer);
            _computerBox = new TextBox
            {
                Location = new Point(16, 78),
                Size = new Size(384, 26),
                Font = new Font("Segoe UI", 10F),
                Text = computer
            };
            card.Controls.Add(_computerBox);

            var lblIp = Helpers.UiModal.FieldLabel("Хост/IP (необязательно)", 16, 110);
            card.Controls.Add(lblIp);
            _ipBox = new TextBox
            {
                Location = new Point(16, 126),
                Size = new Size(384, 26),
                Font = new Font("Segoe UI", 10F),
                Text = ip
            };
            card.Controls.Add(_ipBox);

            var lblGroup = Helpers.UiModal.FieldLabel("Группа", 16, 158);
            card.Controls.Add(lblGroup);
            _groupBox = new ComboBox
            {
                Location = new Point(16, 174),
                Size = new Size(384, 26),
                DropDownStyle = ComboBoxStyle.DropDown,
                Font = new Font("Segoe UI", 10F)
            };
            _groupBox.Items.AddRange((existingGroups ?? System.Array.Empty<string>()).Select(g => (object)g).ToArray());
            _groupBox.Text = group ?? "";
            card.Controls.Add(_groupBox);

            var lblNotes = Helpers.UiModal.FieldLabel("Заметки", 16, 206);
            card.Controls.Add(lblNotes);
            _notesBox = new TextBox
            {
                Location = new Point(16, 222),
                Size = new Size(384, 62),
                Multiline = true,
                Font = new Font("Segoe UI", 10F),
                Text = notes
            };
            card.Controls.Add(_notesBox);

            var okBtn = Helpers.UiButtons.MakeCompactFilled("OK", 100);
            okBtn.Location = new Point(220, 410);
            Controls.Add(okBtn);

            var cancelBtn = Helpers.UiButtons.MakeCompactFilled("Отмена", 100,
                bgOverride: ShadowRDP.Helpers.UiTheme.MutedForeground);
            cancelBtn.Location = new Point(328, 410);
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
                if (string.IsNullOrWhiteSpace(_computerBox.Text))
                {
                    MessageBox.Show(this, "Имя компьютера не может быть пустым.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _computerBox.Focus();
                    return;
                }
                DialogResult = DialogResult.OK;
            }

            okBtn.Click += (_, _) => Submit();
            hiddenOk.Click += (_, _) => Submit();
            cancelBtn.Click += (_, _) => DialogResult = DialogResult.Cancel;

            Shown += (_, _) => _computerBox.Focus();
            _computerBox.SelectAll();
        }
    }
}
