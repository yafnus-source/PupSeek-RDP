using System;
using System.Drawing;
using System.Windows.Forms;

namespace ShadowRDP.Forms
{
    // v3.9.3CY — «Инструменты → Настройки цвета»: все настройки цвета
    // программы в одном окне (перенесены из ПКМ-меню окон):
    //   1. Шапка Даша (hero — Даш/Туду/Монитор)
    //   2. Хедер всплывающих окон (НОВОЕ, независимо от hero)
    //   3. Область шапки RDP
    //   4. Пилы книги (текст — автоконтраст)
    // Каждая секция: образец цвета + «Изменить…» + «Сбросить».
    public class ColorSettingsForm : Form
    {
        public static void Show(IWin32Window owner)
        {
            using var dlg = new ColorSettingsForm();
            Helpers.UiModal.ShowDimmed(owner, dlg);
        }

        private ColorSettingsForm()
        {
            Text = "Настройки цвета";
            ClientSize = new Size(460, 452);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.DialogBackground;

            var hero = Helpers.UiModal.Hero("Настройки цвета", "Шапки, область RDP и пилы книги", "sun");
            Controls.Add(hero);

            var card = Helpers.UiModal.Card(12, 90, 436, 350, "Цвета", "sun", withHeader: false);
            Controls.Add(card);

            int y = 16;
            y = AddRow(card, y, "Шапка Даша",
                () => Helpers.UiTheme.HeroBg,
                () => Helpers.UiModal.PickHeroColor(this),
                () => Helpers.UiTheme.ResetHeroColor());
            y = AddRow(card, y, "Хедер всплывающих окон",
                () => Helpers.UiTheme.DialogHeaderBg,
                () => Helpers.UiModal.PickDialogHeaderColor(this),
                () => Helpers.UiTheme.ResetDialogHeaderColor());
            y = AddRow(card, y, "Область шапки RDP",
                () => Helpers.UiTheme.RdpHeaderBg,
                () => Helpers.UiModal.PickRdpHeaderColor(this),
                () => Helpers.UiTheme.ResetRdpHeaderColor());
            AddRow(card, y, "Пилы книги",
                () => Helpers.UiTheme.PillActiveBg,
                PickPillColor,
                () => Helpers.UiTheme.ResetPillColors());

            // v3.9.5 — «Сохранить гамму»: снимок ВСЕХ текущих цветов как
            // умолчаний ТЕКУЩЕЙ темы (синяя/оливковая — свои). Сбросы строк
            // после этого откатывают к гамме, а не к заводским цветам.
            var gammaBtn = Helpers.UiButtons.MakeCompactFilled("Сохранить гамму", 170);
            gammaBtn.Location = new Point(16, 296);
            gammaBtn.Click += (s, e) =>
            {
                try
                {
                    Helpers.UiTheme.SaveGamma();
                    System.Windows.Forms.MessageBox.Show(this,
                        "Гамма сохранена: текущие цвета стали темой по умолчанию.",
                        "Настройки цвета",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                }
                catch { }
            };
            card.Controls.Add(gammaBtn);
            var gammaNote = new Label
            {
                Text = "своя гамма для синей и оливковой темы",
                Location = new Point(196, 301),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.Transparent
            };
            card.Controls.Add(gammaNote);
        }

        // Одна строка: название, образец цвета, «Изменить…», «Сбросить».
        // Образцы живут с изменением цвета (подписка + отписка).
        private int AddRow(Control parent, int y, string caption,
            Func<Color> getColor, Action pick, Action reset)
        {
            var lbl = new Label
            {
                Text = caption,
                Location = new Point(16, y + 3),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 9F),
                ForeColor = Helpers.UiTheme.Foreground,
                BackColor = Color.Transparent
            };
            parent.Controls.Add(lbl);

            var swatch = new Panel
            {
                Location = new Point(16, y + 24),
                Size = new Size(120, 26),
                BackColor = getColor()
            };
            swatch.Paint += (s, e) =>
            {
                var r = new Rectangle(0, 0, swatch.Width - 1, swatch.Height - 1);
                using var p = Helpers.UiButtons.RoundedRect(r, 6);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillPath(new SolidBrush(swatch.BackColor), p);
                e.Graphics.DrawPath(new Pen(Helpers.UiTheme.Border, 1), p);
            };
            parent.Controls.Add(swatch);

            var btnPick = Helpers.UiButtons.MakeCompactFilled("Изменить…", 100);
            btnPick.Location = new Point(232, y + 24);
            btnPick.Click += (s, e) => { try { pick(); swatch.BackColor = getColor(); swatch.Invalidate(); } catch { } };
            parent.Controls.Add(btnPick);

            var btnReset = Helpers.UiButtons.MakeCompactOutlined("Сбросить", 80);
            btnReset.Location = new Point(340, y + 24);
            btnReset.Click += (s, e) => { try { reset(); swatch.BackColor = getColor(); swatch.Invalidate(); } catch { } };
            parent.Controls.Add(btnReset);

            Action refresh = () =>
            {
                try
                {
                    if (swatch.IsDisposed) return;
                    swatch.BackColor = getColor();
                    swatch.Invalidate();
                }
                catch { }
            };
            Helpers.UiTheme.HeroColorChanged += refresh;
            Helpers.UiTheme.DialogHeaderColorChanged += refresh;
            Helpers.UiTheme.RdpHeaderColorChanged += refresh;
            Helpers.UiTheme.PillColorsChanged += refresh;
            Disposed += (s, e) =>
            {
                try
                {
                    Helpers.UiTheme.HeroColorChanged -= refresh;
                    Helpers.UiTheme.DialogHeaderColorChanged -= refresh;
                    Helpers.UiTheme.RdpHeaderColorChanged -= refresh;
                    Helpers.UiTheme.PillColorsChanged -= refresh;
                }
                catch { }
            };

            return y + 66;
        }

        // Выбор цвета пил книги: текст — автоконтраст (как было в меню группы).
        private void PickPillColor()
        {
            try
            {
                using var dlg = new ColorDialog { AnyColor = true, FullOpen = true };
                try { dlg.Color = Helpers.UiTheme.PillActiveBg; } catch { }
                if (Helpers.UiModal.ShowDimmed(this, dlg) == DialogResult.OK)
                    Helpers.UiTheme.SetPillColors(dlg.Color, Helpers.UiTheme.ContrastText(dlg.Color), dlg.Color);
            }
            catch { }
        }
    }
}
