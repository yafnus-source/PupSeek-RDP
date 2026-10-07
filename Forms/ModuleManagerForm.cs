using System;
using System.Drawing;
using System.Windows.Forms;
using ShadowRDP.Helpers;
using ShadowRDP.Models;
using ShadowRDP.Services;
using Drw = System.Drawing;

namespace ShadowRDP.Forms
{
    // v4.7 — менеджер встраиваемых модулей («Инструменты → Модули»).
    // Список известных модулей: имя, описание, версия, тумблер вкл/выкл.
    // Отключение модуля НЕ удаляет его данные (todo.json сохраняется).
    public class ModuleManagerForm : Form
    {
        private FlowLayoutPanel _list = null!;

        public ModuleManagerForm()
        {
            Text = "Модули";
            BackColor = UiTheme.DialogBackground;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 420);
            MinimumSize = new Size(480, 320);
            FormBorderStyle = FormBorderStyle.Sizable;

            var title = new Label
            {
                Text = "Модули: опциональные фичи, ставятся по желанию",
                Font = new Drw.Font("Segoe UI Semibold", 10f),
                ForeColor = UiTheme.Foreground,
                BackColor = Drw.Color.Transparent,
                AutoSize = true,
                Location = new Point(16, 14)
            };
            Controls.Add(title);

            var hint = new Label
            {
                Text = "Выключенный модуль убирает кнопку из сайдбара, пункт из трея и останавливает свои сервисы. Данные модуля сохраняются.",
                Font = new Drw.Font("Segoe UI", 8.5f),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Drw.Color.Transparent,
                AutoSize = false,
                Size = new Size(528, 34),
                Location = new Point(16, 40)
            };
            Controls.Add(hint);

            _list = new FlowLayoutPanel
            {
                Location = new Point(12, 78),
                Size = new Size(536, 330),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = UiTheme.DialogBackground
            };
            Controls.Add(_list);

            Resize += (s, e) =>
            {
                try { _list.Width = ClientSize.Width - 24; _list.Height = ClientSize.Height - 90; } catch { }
            };

            Reload();
            ModuleManager.StateChanged += OnModuleStateChanged;
            FormClosed += (s, e) => { try { ModuleManager.StateChanged -= OnModuleStateChanged; } catch { } };
        }

        private void OnModuleStateChanged(string id, bool enabled)
        {
            if (IsDisposed || Disposing) return;
            try { if (InvokeRequired) BeginInvoke(new Action(Reload)); else Reload(); } catch { }
        }

        private void Reload()
        {
            try
            {
                _list.Controls.Clear();
                foreach (var m in ModuleManager.All)
                    _list.Controls.Add(BuildCard(m));
            }
            catch { }
        }

        private Control BuildCard(AppModule m)
        {
            bool on = false;
            try { on = ModuleManager.IsEnabled(m.Id); } catch { }

            var card = new Panel
            {
                Width = Math.Max(300, _list.ClientSize.Width - 8),
                Height = 96,
                BackColor = UiTheme.DialogCard,
                Margin = new Padding(0, 0, 0, 8)
            };

            var name = new Label
            {
                Text = m.Name,
                Font = new Drw.Font("Segoe UI Semibold", 11f),
                ForeColor = UiTheme.Foreground,
                BackColor = Drw.Color.Transparent,
                AutoSize = true,
                Location = new Point(14, 10)
            };
            card.Controls.Add(name);

            var ver = new Label
            {
                Text = "v" + m.Version,
                Font = new Drw.Font("Segoe UI", 8f),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Drw.Color.Transparent,
                AutoSize = true,
                Location = new Point(name.Right + 8, 14)
            };
            card.Controls.Add(ver);

            var state = new Label
            {
                Text = on ? "● включён" : "○ выключен",
                Font = new Drw.Font("Segoe UI", 8.5f),
                ForeColor = on ? Drw.Color.FromArgb(0xF0, 0xA0, 0x50) : UiTheme.MutedForeground,
                BackColor = Drw.Color.Transparent,
                AutoSize = true,
                Location = new Point(14, 32)
            };
            card.Controls.Add(state);

            var desc = new Label
            {
                Text = m.Description,
                Font = new Drw.Font("Segoe UI", 8.5f),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Drw.Color.Transparent,
                AutoSize = false,
                Size = new Size(card.Width - 150, 30),
                Location = new Point(14, 52)
            };
            card.Controls.Add(desc);

            var btn = Helpers.UiButtons.MakeCompactFilled(on ? "Выключить" : "Включить", 100);
            btn.Location = new Point(card.Width - 116, 30);
            var captured = m;
            btn.Click += (s, e) =>
            {
                try
                {
                    bool cur;
                    try { cur = ModuleManager.IsEnabled(captured.Id); } catch { cur = false; }
                    ModuleManager.SetEnabled(captured.Id, !cur);
                }
                catch { }
            };
            card.Controls.Add(btn);

            card.Resize += (s, e) =>
            {
                try { btn.Location = new Point(card.Width - 116, 30); desc.Width = card.Width - 150; } catch { }
            };
            return card;
        }
    }
}
