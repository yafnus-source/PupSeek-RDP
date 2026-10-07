using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ShadowRDP.Controls;

namespace ShadowRDP.Helpers
{
    // Единый поиск для всех окон: подпись «Поиск» + однострочное поле
    // с нативным cue-баннером + крестик очистки. Нативный кью рисует
    // сама ОС внутри поля — наложиться на подпись он не может;
    // оверлей-плейсхолдера больше нет (это был источник «Поиск Поиск»,
    // наложений и каретки в середине).
    public static class UiSearchBox
    {
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private sealed class State
        {
            public CaretHidingTextBox Box = null!;
            public Label Clear = null!;
            public Timer? Debounce;
            public string Cue = "";
            public Color IdleBg;
        }

        public static Panel Create(string placeholder, Action<string> onSearch, int width = 640, int height = 32, bool withDebounce = true, Color? background = null)
        {
            float fontSz = height >= 32 ? 12F : height >= 27 ? 10F : 9F;
            var font = new Font("Segoe UI", fontSz);
            // Высота однострочного поля диктуется шрифтом — центрируем
            // +3px вниз (кью и текст визуально ниже центра).
            int boxH = TextRenderer.MeasureText("Ag", font).Height + 7;
            int boxY = Math.Max(2, (height - boxH) / 2) + 3;
            int clearSz = Math.Max(16, height - 8);
            float clearSzF = height >= 32 ? 11F : 9F;
            var bgFill = background ?? UiTheme.Muted;
            var wrapper = new Panel
            {
                Size = new Size(width, height),
                BackColor = Color.Transparent,
                MinimumSize = new Size(150, height),
                MaximumSize = new Size(640, height)
            };
            // v4.8.1 — фон гладкой отрисовкой (был Region: лесенка).
            Color wbg = bgFill;
            UiButtons.EnableSmoothFill(wrapper, () => wbg, 6);

            // Подписи нет — только контекстная подсказка (кью) в поле.
            // Так задвоение исключено физически (один текстовый элемент).
            int boxX = 10;

            var st = new State { IdleBg = bgFill };
            var box = new CaretHidingTextBox
            {
                Location = new Point(boxX, boxY),
                Size = new Size(width - boxX - 40, boxH),
                Multiline = false,
                BorderStyle = BorderStyle.None,
                BackColor = bgFill,
                ForeColor = UiTheme.Foreground,
                Font = font,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                // Каретка скрыта всегда (мигание | убрано по требованию).
                ShowCaretOnFocus = false
            };
            st.Box = box;

            var clear = new Label
            {
                Text = "✕",
                Location = new Point(width - 10 - clearSz, 4),
                Size = new Size(clearSz, clearSz),
                Font = new Font("Segoe UI Symbol", clearSzF, FontStyle.Bold),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            st.Clear = clear;
            clear.Click += (s, e) => { box.Text = ""; box.Focus(); };
            clear.MouseEnter += (s, e) => clear.ForeColor = UiTheme.Foreground;
            clear.MouseLeave += (s, e) => clear.ForeColor = UiTheme.MutedForeground;

            void ApplyCue()
            {
                try
                {
                    if (box.IsHandleCreated)
                        SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, st.Cue ?? "");
                }
                catch { }
            }
            st.Cue = placeholder ?? "";
            box.HandleCreated += (s, e) => ApplyCue();

            Action focusBox = () =>
            {
                try
                {
                    box.Focus();
                    // Клик мышкой: фон светлее (белый) сразу, не дожидаясь Enter.
                    wbg = Color.White;
                    box.BackColor = Color.White;
                    wrapper.Invalidate();
                }
                catch { }
            };
            wrapper.Click += (s, e) => focusBox();

            // keep clear at right on resize
            wrapper.Resize += (s, e) =>
            {
                try
                {
                    int cs = Math.Max(16, wrapper.Height - 8);
                    clear.Location = new Point(wrapper.Width - 10 - cs, 4);
                    clear.Size = new Size(cs, cs);
                    box.Size = new Size(wrapper.Width - boxX - 40, box.Height);
                    wrapper.Invalidate();
                }
                catch { }
            };

            box.Enter += (s, e) =>
            {
                // Активация: поле пустеет + белый фон.
                if (!string.IsNullOrEmpty(box.Text)) box.Text = "";
                wbg = Color.White; box.BackColor = Color.White; wrapper.Invalidate();
            };
            box.Leave += (s, e) => { wbg = st.IdleBg; box.BackColor = st.IdleBg; wrapper.Invalidate(); };
            // Enter в однострочном поле: без бипа, поиск сразу.
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    try
                    {
                        if (withDebounce) { st.Debounce?.Stop(); }
                        onSearch(box.Text.Trim());
                    }
                    catch { }
                }
            };

            Timer? debounce = null;
            if (withDebounce)
            {
                debounce = new Timer { Interval = 120 };
                debounce.Tick += (s, e) => { debounce.Stop(); onSearch(box.Text.Trim()); };
                st.Debounce = debounce;
            }

            box.TextChanged += (s, e) =>
            {
                bool has = !string.IsNullOrEmpty(box.Text);
                clear.Visible = has;
                if (withDebounce) { debounce!.Stop(); debounce.Start(); }
                else onSearch(box.Text.Trim());
            };

            wrapper.Controls.Add(box);
            wrapper.Controls.Add(clear);
            clear.BringToFront();

            wrapper.Tag = st;
            // Кью после создания хендла (первый ApplyCue — через HandleCreated).
            wrapper.HandleCreated += (s, e) => ApplyCue();
            return wrapper;
        }

        private static State? GetState(Panel wrapper) => wrapper.Tag as State;

        public static TextBox GetBox(Panel wrapper)
        {
            // Совместимость со старым Tag-массивом.
            if (wrapper.Tag is object[] arr && arr.Length > 0 && arr[0] is TextBox tb) return tb;
            return GetState(wrapper)?.Box ?? wrapper.Controls.OfType<TextBox>().FirstOrDefault()!;
        }

        // Контекстная подсказка поля («Фильтр по хосту…» и т.п.).
        public static void SetCue(Panel wrapper, string cue)
        {
            var st = GetState(wrapper);
            if (st == null) return;
            st.Cue = cue ?? "";
            try
            {
                if (st.Box.IsHandleCreated)
                    SendMessage(st.Box.Handle, EM_SETCUEBANNER, (IntPtr)1, st.Cue);
            }
            catch { }
        }

        // Программная установка текста (каретка в конец).
        public static void SetText(Panel wrapper, string text)
        {
            var st = GetState(wrapper);
            if (st == null) return;
            try
            {
                st.Box.Text = text ?? "";
                st.Box.SelectionStart = st.Box.Text.Length;
                st.Box.SelectionLength = 0;
            }
            catch { }
        }

        // Останов и освобождение внутреннего дебаунс-таймера.
        public static void Dispose(Panel wrapper)
        {
            try
            {
                var st = GetState(wrapper);
                if (st?.Debounce == null) return;
                st.Debounce.Stop();
                st.Debounce.Dispose();
                st.Debounce = null;
            }
            catch { }
        }
    }
}
