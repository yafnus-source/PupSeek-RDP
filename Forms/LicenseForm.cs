using System;
using System.Drawing;
using System.Windows.Forms;
using ShadowRDP.Helpers;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // v4.0 — окно «Инструменты → Лицензия…»: план (8 модулей + статусы),
    // ввод ключа, пробный период. Цены — строки каталога (Price).
    public class LicenseForm : Form
    {
        private readonly Panel _planCard;
        private readonly Label _statusLabel;
        private TextBox _keyBox = null!;

        public static void Show(IWin32Window owner)
        {
            using var f = new LicenseForm();
            UiModal.ShowDimmed(owner, f);
        }

        public LicenseForm()
        {
            Text = "Лицензия";
            ClientSize = new Size(620, 560);
            MinimumSize = new Size(540, 460);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = UiTheme.DialogBackground;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); } };

            var hero = UiModal.Hero("Лицензия",
                "модули программы, пробный период, ключ", "key");
            hero.Dock = DockStyle.Top;
            Controls.Add(hero);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
            footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var closeBtn = UiButtons.MakeCompactFilled("Закрыть", 120);
            closeBtn.Click += (s, e) => Close();
            footer.Controls.Add(closeBtn);
            footer.Resize += (s, e) =>
            {
                try
                {
                    closeBtn.Location = new Point(footer.ClientSize.Width - 16 - closeBtn.Width, 15);
                    _statusLabel.Width = Math.Max(60, footer.ClientSize.Width - 32 - closeBtn.Width - 16);
                }
                catch { }
            };
            _statusLabel = new Label
            {
                Location = new Point(16, 15),
                Size = new Size(300, 22),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = ""
            };
            footer.Controls.Add(_statusLabel);
            Controls.Add(footer);

            var body = new Panel
            {
                Location = new Point(0, 80),
                Size = new Size(620, 428),
                BackColor = UiTheme.DialogBackground
            };
            _planCard = UiModal.Card(0, 0, 100, 100, "Ваш план", "list", withHeader: true);
            body.Controls.Add(_planCard);

            var keyCard = UiModal.Card(0, 0, 100, 100, "Ключ", "key", withHeader: true);
            var keyCap = UiModal.FieldLabel("Ключ вида PSR1-… . …", 0, 0);
            keyCard.Controls.Add(keyCap);
            var keyBox = new TextBox
            {
                Multiline = false,
                TabStop = false,
                BackColor = Color.White,
                ForeColor = UiTheme.Foreground
            };
            var keyWrap = UiModal.Field(keyBox, 300, 30);
            keyBox.Font = new Font("Segoe UI", 9F);
            keyCard.Controls.Add(keyWrap);
            _keyBox = keyBox;
            var actBtn = UiButtons.MakeCompactFilled("Активировать", 130);
            actBtn.Click += (s, e) => Activate();
            keyCard.Controls.Add(actBtn);
            var contact = new Label
            {
                AutoSize = false,
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Text = "Ключи: модуль — 500 ₽, пакеты «Мониторинг» и «Учёт» — по 990 ₽, «Про» (все модули) — 2490 ₽. Напишите нам, вышлем строку."
            };
            keyCard.Controls.Add(contact);
            body.Controls.Add(keyCard);
            Controls.Add(body);

            Resize += (s, e) => LayoutBody();
            LayoutBody();
            Shown += (s, e) => LayoutBody();
            RefreshPlan();

            void LayoutBody()
            {
                try
                {
                    body.Width = ClientSize.Width;
                    body.Height = Math.Max(200, ClientSize.Height - 80 - 52);
                    const int m = 14;
                    int contentW = Math.Max(240, body.Width - m * 2);
                    int innerW = contentW - 32;
                    // Карточка плана: шапка 56 + по 26 на модуль + низ 14.
                    int planH = 56 + ModuleCatalog.All.Count * 26 + 14;
                    _planCard.Location = new Point(m, 12);
                    _planCard.Size = new Size(contentW, planH);
                    keyCard.Location = new Point(m, 12 + planH + 12);
                    keyCard.Size = new Size(contentW, 150);
                    keyCap.Location = new Point(16, 56);
                    keyCap.Size = new Size(innerW, 16);
                    keyWrap.Location = new Point(16, 76);
                    keyWrap.Width = Math.Max(200, innerW - 150);
                    actBtn.Location = new Point(16 + keyWrap.Width + 8, 79);
                    contact.Location = new Point(16, 112);
                    contact.Size = new Size(innerW, 30);
                    contact.MaximumSize = new Size(innerW, 0);
                    contact.AutoSize = true;
                    foreach (Control c in _planCard.Controls)
                    {
                        if (c is Label l && l.Tag as string == "planrow")
                            l.Width = innerW;
                    }
                }
                catch { }
            }
        }

        private void RefreshPlan()
        {
            try
            {
                var keep = new System.Collections.Generic.List<Control>();
                foreach (Control c in _planCard.Controls)
                    if (c is Label l && l.Tag as string == "planrow") keep.Add(c);
                foreach (var c in keep) { _planCard.Controls.Remove(c); c.Dispose(); }
                var active = LicenseStore.ActiveModules();
                var lic = LicenseStore.Load();
                int y = 56;
                foreach (var m in ModuleCatalog.All)
                {
                    string status;
                    if (active.Contains(m.Id))
                        status = lic.Trial ? $"Пробный{(lic.Exp > 0 ? " до " + DateTimeOffset.FromUnixTimeSeconds(lic.Exp).ToLocalTime().ToString("dd.MM.yyyy") : "")}" : "Активен";
                    else
                        status = "—";
                    var l = new Label
                    {
                        Text = $"{m.Title} — {status}" + (string.IsNullOrEmpty(m.Price) ? "" : $"  ({m.Price})"),
                        Tag = "planrow",
                        Location = new Point(16, y),
                        Size = new Size(400, 22),
                        Font = new Font("Segoe UI", 9F),
                        ForeColor = active.Contains(m.Id) ? UiTheme.Foreground : UiTheme.MutedForeground,
                        BackColor = Color.Transparent,
                        TextAlign = ContentAlignment.MiddleLeft
                    };
                    _planCard.Controls.Add(l);
                    y += 26;
                }
            }
            catch { }
        }

        private void Activate()
        {
            try
            {
                var (data, err) = LicenseStore.Validate(_keyBox.Text);
                if (data == null)
                {
                    _statusLabel.Text = err ?? "Ключ не подошёл.";
                    return;
                }
                data.TrialReminded = false;
                LicenseStore.Save(data);
                RefreshPlan();
                _statusLabel.Text = data.Trial
                    ? "Пробный ключ принят."
                    : $"Принято: модулей {data.Modules.Count}" +
                      (data.Exp > 0 ? " до " + DateTimeOffset.FromUnixTimeSeconds(data.Exp).ToLocalTime().ToString("dd.MM.yyyy") : " (бессрочно)");
                try { Logger.Info($"License activated: {data.Modules.Count} modules"); } catch { }
            }
            catch (Exception ex) { _statusLabel.Text = $"Не активировалось: {ex.Message}"; }
        }
    }

    // Мягкий апсейл: пункт ведёт сюда, а не пропадает.
    public static class LicenseUpsell
    {
        public static void Show(IWin32Window owner, string module)
        {
            try
            {
                string title = Services.ModuleCatalog.TitleOf(module);
                using var dlg = new Form
                {
                    Text = $"Модуль «{title}»",
                    ClientSize = new Size(460, 250),
                    MinimumSize = new Size(420, 230),
                    MaximumSize = new Size(520, 300),
                    StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.Sizable,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    Font = new Font("Segoe UI", 9F),
                    BackColor = UiTheme.DialogBackground,
                    KeyPreview = true
                };
                dlg.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; dlg.Close(); } };
                var hero = UiModal.Hero($"Модуль «{title}»", "платный модуль версии 4.x", "key");
                hero.Dock = DockStyle.Top;
                dlg.Controls.Add(hero);
                var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UiTheme.DialogCard };
                footer.Paint += (s, e) => { using var pen = new Pen(UiTheme.Border, 1); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
                var licBtn = UiButtons.MakeCompactFilled("Открыть лицензию", 170);
                licBtn.Click += (s, e) => { try { dlg.Close(); } catch { } try { LicenseForm.Show(owner); } catch { } };
                var closeBtn = UiButtons.MakeCompactFilled("Закрыть", 100, bgOverride: UiTheme.MutedForeground);
                closeBtn.Click += (s, e) => dlg.Close();
                footer.Controls.Add(licBtn);
                footer.Controls.Add(closeBtn);
                footer.Resize += (s, e) =>
                {
                    try
                    {
                        closeBtn.Location = new Point(footer.ClientSize.Width - 16 - closeBtn.Width, 15);
                        licBtn.Location = new Point(16, 15);
                    }
                    catch { }
                };
                dlg.Controls.Add(footer);
                var body = new Label
                {
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 9F),
                    ForeColor = UiTheme.Foreground,
                    BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Padding = new Padding(24, 0, 24, 0),
                    Text = $"«{title}» — платный модуль.\nПервые 10 дней всё было бесплатно (пробный период).\nОткройте лицензию и введите ключ."
                };
                dlg.Controls.Add(body);
                UiModal.ShowDimmed(owner, dlg);
            }
            catch { }
        }
    }
}
