using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ShadowRDP.Controls;

namespace ShadowRDP.Helpers
{
    // v4.3.2 — "sample modal" toolkit. Builds the modern dialog look used as
    // the template for every modal window:
    //   • Gradient hero band with icon chip, title/subtitle and a close ✕.
    //   • Rounded section cards (title + icon header, warm border).
    //   • Rounded input fields (TextBox / ComboBox / NumericUpDown) with
    //     hover/focus border highlight.
    //
    // Design notes:
    //   form.BackColor      = UiTheme.Background   (cards sit on softer bg)
    //   card.BackColor      = UiTheme.Card          (section surfaces)
    //   field.BackColor     = Color.White           (inputs pop on the card)
    //   hero gradient       = HeaderBar → darkened   (both themes look good)
    public static class UiModal
    {
        // 40px rounded hero band with icon chip, title, subtitle and optional ✕ close.
        // onClose == null → без крестика (закрытие штатной кнопкой окна).
        public static Panel Hero(string title, string subtitle, string iconKey, Action? onClose = null)
        {
            // v3.9.3CY — хедер диалогов НЕ зависит от hero Даша: свой цвет
            // DialogHeaderBg (по умолчанию синий HeaderBar темы).
            var band = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = UiTheme.DialogHeaderBg };
            band.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, band.Width, band.Height);
                var top = UiTheme.DialogHeaderBg;
                // На почти белом фоне градиент не затемняем (давал серую полосу).
                double lum = (0.299 * top.R + 0.587 * top.G + 0.114 * top.B) / 255.0;
                var bottom = lum > 0.9 ? top : ControlPaint.Dark(top, 0.18f);
                using (var br = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical))
                    g.FillRectangle(br, rect);
                using var pen = new Pen(Color.FromArgb(30, 0, 0, 0), 1);
                g.DrawLine(pen, 0, band.Height - 1, band.Width, band.Height - 1);
            };
            // Живой отклик на смену цвета хедера диалогов (с отпиской).
            Action refreshHero = () =>
            {
                try
                {
                    if (band.IsDisposed) return;
                    band.BackColor = UiTheme.DialogHeaderBg;
                    band.Invalidate(true);
                }
                catch { }
            };
            UiTheme.DialogHeaderColorChanged += refreshHero;
            band.Disposed += (s, e) => { try { UiTheme.DialogHeaderColorChanged -= refreshHero; } catch { } };
            // v3.9.3BJ — диалоги ОТДЕЛЬНО: всегда HeaderBar темы (выбор hero
            // красит только шапки дашбордов/туду, не всплывашки).

            // Icon chip: soft-white rounded square with the tinted icon.
            var chip = new Panel { Location = new Point(18, 18), Size = new Size(44, 44), BackColor = Color.Transparent };
            chip.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = RoundedRect(new Rectangle(0, 0, chip.Width - 1, chip.Height - 1), 10);
                using var brush = new SolidBrush(Color.FromArgb(38, 255, 255, 255));
                g.FillPath(brush, path);
                using var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1);
                g.DrawPath(pen, path);
                // v3.9.3CY — глиф в цвет текста хедера диалогов.
                // v3.9.3 — нативный рендер 24 (было растягивание 16→24 = мыло).
                var raw = MenuIcons.GetScaled(iconKey, 24);
                var icon = UiTheme.DialogHeaderFg.ToArgb() != Color.White.ToArgb() ? raw : UiButtons.TintWhite(raw);
                var iconSz = 24;
                // v3.9.3 — качественный масштаб 16→24: без bicubic иконка «мыльная».
                var imPrev = g.InterpolationMode;
                var poPrev = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(icon, new Rectangle((chip.Width - iconSz) / 2, (chip.Height - iconSz) / 2, iconSz, iconSz));
                g.InterpolationMode = imPrev;
                g.PixelOffsetMode = poPrev;
            };

            var lblTitle = new Label
            {
                Text = title,
                Location = new Point(72, 16),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 12.5f),
                // v3.9.3CY — авоконтраст к хедеру диалогов.
                ForeColor = UiTheme.DialogHeaderFg,
                BackColor = Color.Transparent
            };
            var sub = new Label
            {
                Text = subtitle,
                Location = new Point(73, 44),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = UiTheme.DialogHeaderFg,
                BackColor = Color.Transparent
            };

            if (onClose != null)
            {
                var close = new Button
                {
                    Text = "✕",
                    Location = new Point(band.Width - 40, 14),
                    Size = new Size(28, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.Transparent,
                    ForeColor = UiTheme.DialogHeaderFg,
                    Font = new Font("Segoe UI Semibold", 11f),
                    TabStop = false,
                    Cursor = Cursors.Hand
                };
                close.FlatAppearance.BorderSize = 0;
                close.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 255, 255, 255);
                close.FlatAppearance.MouseDownBackColor = Color.FromArgb(70, 255, 255, 255);
                close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                close.Click += (s, e) => onClose?.Invoke();
                band.Controls.Add(close);
            }

            band.Controls.Add(chip);
            band.Controls.Add(lblTitle);
            band.Controls.Add(sub);
            return band;
        }

        // Rounded section card with a title header (icon chip + accent title).
        // Content goes inside with offset (16, 44).
        // withHeader: false — карточка без шапки (только скруглённая рамка),
        // контент кладётся от (16, 14).
        public static Panel Card(int x, int y, int width, int height, string title, string iconKey, bool withHeader = true)
        {
            // v3.9.3AC — подложка Transparent (была Card): квадратные почти
            // белые углы панели торчали на кремовой странице белыми
            // треугольниками. Цвет даёт гладкий фил в Paint ниже.
            var p = new Panel { Location = new Point(x, y), Size = new Size(width, height), BackColor = Color.Transparent };
            p.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
        using var path = RoundedRect(rect, 10);
        // v3.9.3CJ — карточки диалогов в синей теме белые.
        using (var brush = new SolidBrush(UiTheme.DialogCard)) g.FillPath(brush, path);
        using (var pen = new Pen(UiTheme.Border, 1)) g.DrawPath(pen, path);

                // Title header: icon chip + accent title.
                if (!withHeader) return;
                var chip = new Rectangle(16, 13, 24, 24);
                // v3.9.3Z — подложка иконки в тон страницы (#E8E4D2),
                // была AccentSoft #E8ECE8 (не сочеталась).
                using (var chipPath = RoundedRect(chip, 7))
                using (var chipBrush = new SolidBrush(UiTheme.DialogBackground))
                    g.FillPath(chipBrush, chipPath);
                var icon = MenuIcons.GetScaled(iconKey, 14);
                var iconSz = 14;
                var iconRect = new Rectangle(chip.X + (24 - iconSz) / 2, chip.Y + (24 - iconSz) / 2, iconSz, iconSz);
                if (icon != null)
                {
                    float im = 0.55f;
                    using var iconAttrs = new System.Drawing.Imaging.ImageAttributes();
                    iconAttrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new[]
                    {
                        new[] { im, 0f, 0f, 0f, 0f },
                        new[] { 0f, im, 0f, 0f, 0f },
                        new[] { 0f, 0f, im, 0f, 0f },
                        new[] { 0f, 0f, 0f, 1f, 0f },
                        new[] { 0f, 0f, 0f, 0f, 1f }
                    }));
                    // v3.9.3 — качественный масштаб 16→14 (был Default).
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(icon, iconRect, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, iconAttrs);
                }
                // v3.9.3AQ — чёткий ClearType заголовка (голый DrawString
                // давал «пиксельные» глифы; SmoothingMode на текст не влияет).
                // v3.9.3AR — TextRenderer как у всех подписей: попиксельно
                // одинаково с остальным UI (GDI+ DrawString всегда зернистее).
                using (var titleFont = new Font("Segoe UI Semibold", 9.5f))
                {
                    var titleColor = ControlPaint.Dark(UiTheme.Accent, 0.5f);
                    TextRenderer.DrawText(g, title, titleFont, new Point(48, 14), titleColor,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                }
                using (var pen = new Pen(UiTheme.BorderSubtle, 1))
                    g.DrawLine(pen, 16, 44, p.Width - 16, 44);
            };
            return p;
        }

        // Small muted label shown above an input field.
        public static Label FieldLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 7.5f),
                ForeColor = UiTheme.MutedForeground,
                BackColor = Color.Transparent
            };
        }

        // White rounded field wrapping a borderless editor. Border highlights
        // on hover and turns accent on focus. Returns the wrapper panel;
        // its editor is positioned automatically to fill the interior.
        // v3.9.3 — wrapper BackColor Transparent (был White): квадратные
        // углы обёртки торчали белым из-под скруглённой заливки r=7.
        // Белый рисуется только внутри скруглённого пути в OnFieldPaint.
        public static Panel Field(Control editor, int width, int height = 30)
        {
            var wrap = new Panel
            {
                BackColor = Color.Transparent,
                Size = new Size(width, height),
                Padding = new Padding(9, 4, 9, 4)
            };
            wrap.Paint += OnFieldPaint;

            wrap.Controls.Add(editor);
            editor.Location = new Point(wrap.Padding.Left, wrap.Padding.Top);
            editor.Width = width - wrap.Padding.Left - wrap.Padding.Right;
            editor.Height = height - wrap.Padding.Top - wrap.Padding.Bottom;
            wrap.Resize += (s, e) =>
            {
                editor.Location = new Point(wrap.Padding.Left, wrap.Padding.Top);
                editor.Width = wrap.Width - wrap.Padding.Left - wrap.Padding.Right;
                editor.Height = wrap.Height - wrap.Padding.Top - wrap.Padding.Bottom;
            };

            switch (editor)
            {
                case TextBox tb:
                    tb.BorderStyle = BorderStyle.None;
                    tb.BackColor = Color.White;
                    tb.Font = new Font("Segoe UI", 9.5f);
                    break;
                case ComboBox cb:
                    cb.FlatStyle = FlatStyle.Flat;
                    cb.BackColor = Color.White;
                    cb.Font = new Font("Segoe UI", 9.5f);
                    break;
                case NumericUpDown nud:
                    nud.BorderStyle = BorderStyle.None;
                    nud.BackColor = Color.White;
                    nud.Font = new Font("Segoe UI", 9.5f);
                    break;
            }

            wrap.GotFocus += (s, e) => { try { editor.Focus(); } catch { } };
            editor.GotFocus += (s, e) => { wrap.Invalidate(); };
            editor.LostFocus += (s, e) => { wrap.Invalidate(); };
            wrap.MouseEnter += (s, e) => { if (!wrap.Focused) wrap.Invalidate(); };
            wrap.MouseLeave += (s, e) => wrap.Invalidate();
            editor.Tag = wrap;

            return wrap;
        }

        private static void OnFieldPaint(object? sender, PaintEventArgs e)
        {
            var wrap = (Panel)sender!;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, wrap.Width - 1, wrap.Height - 1);
        using var path = RoundedRect(rect, 7);
        using (var brush = new SolidBrush(Color.White)) g.FillPath(brush, path);

            bool anyFocused = wrap.Focused;
            if (wrap.Controls.Count > 0) anyFocused = anyFocused || wrap.Controls[0].Focused;
            var border = anyFocused ? UiTheme.Accent : (wrap.ClientRectangle.Contains(wrap.PointToClient(Control.MousePosition))
                ? ControlPaint.Dark(UiTheme.Border, 0.08f) : UiTheme.Border);
            using (var pen = new Pen(border, anyFocused ? 1.5f : 1f)) g.DrawPath(pen, path);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0) { path.AddRectangle(bounds); return path; }
            var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // DPI-proportional scaling for the sample modal.
        //
        // The app runs HighDpiMode.SystemAware: display fonts scale with the
        // monitor DPI automatically, but control coordinates stay in design
        // pixels — so on 125%/150% displays fixed-pixel labels/buttons overlap
        // their neighbours. ApplyDpiScale rescales every control's position
        // and size by the same factor the fonts use, keeping the layout intact.
        // Returns the factor (1.0 on 100% displays = no-op).
        public static float ApplyDpiScale(Control root)
        {
            float k = 1f;
            using (var g = root.CreateGraphics()) k = g.DpiX / 96f;
            if (k <= 1.001f) return 1f;
            ScaleTree(root, k);
            return k;
        }

        // Затемнение фона под модалками: полупрозрачный чёрный оверлей на
        // весь owner, поверх — диалог. Вложенные модалки двойного затемнения
        // не дают (счётчик глубины). MessageBox — вне скоупа (намеренно).
        private static int _dimDepth = 0;

        // Оверлей не забирает фокус (модалка возьмёт сама).
        private sealed class DimOverlay : Form
        {
            protected override bool ShowWithoutActivation => true;
        }

        private static Form? ResolveDimOwner(IWin32Window? owner)
        {
            try
            {
                if (owner is Form f && !f.IsDisposed && f.Visible) return f;
                var a = Form.ActiveForm;
                if (a != null && !a.IsDisposed && a.Visible) return a;
            }
            catch { }
            return null;
        }

        private static Form? OpenDim(Form of)
        {
            try
            {
                // Встроенная форма (книга/даш в менеджере) не может быть
                // owner'ом оверлея — берём top-level окно (весь менеджер).
                var top = of.TopLevelControl as Form ?? of;
                var dim = new DimOverlay
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    BackColor = Color.Black,
                    Opacity = 0.45,
                    StartPosition = FormStartPosition.Manual,
                    Bounds = top.Bounds,
                    TopMost = top.TopMost
                };
                // v3.9.3AR — оверлею тоже без анимаций (иначе 1px-швы закрытия).
                try { DisableTransitions(dim); } catch { }
                dim.Show(top);
                return dim;
            }
            catch { return null; }
        }

        private static void ScaleTree(Control c, float k)
        {
            foreach (Control child in c.Controls)
            {
                child.Location = new Point((int)Math.Round(child.Location.X * k), (int)Math.Round(child.Location.Y * k));
                child.Size = new Size((int)Math.Round(child.Size.Width * k), (int)Math.Round(child.Size.Height * k));
                if (child.Controls.Count > 0) ScaleTree(child, k);
            }
        }

        // v3.9.3AO — отключаем DWM-анимации открытия/закрытия модалок:
        // close-анимация успевает попортить композитор ПОСЛЕ нашего репейнта
        // (тёмные 1px-остатки на контрастных краях ячеек, умирают только от
        // следующей перерисовки). Диалоги просто появляются/исчезают мгновенно.
        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
        public static void DisableTransitions(System.Windows.Forms.Form dlg)
        {
            try
            {
                if (dlg == null || dlg.IsDisposed) return;
                int on = 1;
                DwmSetWindowAttribute(dlg.Handle, 3 /*DWMWA_TRANSITIONS_FORCEDISABLED*/, ref on, 4);
            }
            catch { }
        }

        // v3.9.3BD — модалка с гарантированным выходом по Esc: перехват
        // на уровне ProcessDialogKey (не зависит от фокуса — в попапе Деталей
        // вообще нет фокусируемых контролов, и KeyPreview-вариант терялся).
        public class EscModalForm : Form
        {
            protected override bool ProcessDialogKey(Keys keyData)
            {
                if (keyData == Keys.Escape)
                {
                    try { DialogResult = DialogResult.Cancel; Close(); } catch { }
                    return true;
                }
                return base.ProcessDialogKey(keyData);
            }
        }

        // v3.9.3BH — выбор цвета hero (как пилы в книге): ColorDialog,
        // применяется мгновенно + персист в colors.json.
        public static void PickHeroColor(System.Windows.Forms.IWin32Window? owner)
        {
            try
            {
                using var dlg = new ColorDialog { AnyColor = true, FullOpen = true };
                try { dlg.Color = UiTheme.HeroBg; } catch { }
                if (ShowDimmed(owner, dlg) == DialogResult.OK)
                    UiTheme.SetHeroColor(dlg.Color);
            }
            catch { }
        }

        // v3.9.3CY — цвет хедера всплывающих окон (независим от hero).
        public static void PickDialogHeaderColor(System.Windows.Forms.IWin32Window? owner)
        {
            try
            {
                using var dlg = new ColorDialog { AnyColor = true, FullOpen = true };
                try { dlg.Color = UiTheme.DialogHeaderBg; } catch { }
                if (ShowDimmed(owner, dlg) == DialogResult.OK)
                    UiTheme.SetDialogHeaderColor(dlg.Color);
            }
            catch { }
        }

        // v3.9.3CM — эксперимент: цвет области шапки RDP (как hero в Даше).
        public static void PickRdpHeaderColor(System.Windows.Forms.IWin32Window? owner)
        {
            try
            {
                using var dlg = new ColorDialog { AnyColor = true, FullOpen = true };
                try { dlg.Color = UiTheme.RdpHeaderBg; } catch { }
                if (ShowDimmed(owner, dlg) == DialogResult.OK)
                    UiTheme.SetRdpHeaderColor(dlg.Color);
            }
            catch { }
        }

        public static DialogResult ShowDimmed(IWin32Window? owner, CommonDialog dlg)
        {
            var of = ResolveDimOwner(owner);
            Form? dim = null;
            bool pushed = false;
            try
            {
                if (of != null)
                {
                    if (_dimDepth == 0) dim = OpenDim(of);
                    _dimDepth++;
                    pushed = true;
                    return dlg.ShowDialog(of);
                }
                return dlg.ShowDialog();
            }
            finally
            {
                if (pushed) _dimDepth = Math.Max(0, _dimDepth - 1);
                try { dim?.Close(); dim?.Dispose(); } catch { }
            }
        }
        public static DialogResult ShowDimmed(IWin32Window? owner, Form dlg)
        {
            // v3.9.3AO — без DWM-анимаций (см. DisableTransitions).
            try { DisableTransitions(dlg); } catch { }
            // v3.9.3BC — выход по Esc из ЛЮБОЙ модалки централизованно
            // (у диалогов без своего KeyPreview его не было вообще).
            // Открытый дропдаун комбо закрывается сам, диалог не трогаем.
            try
            {
                dlg.KeyPreview = true;
                dlg.KeyDown += (s2, e2) =>
                {
                    try
                    {
                        if (e2.KeyCode != Keys.Escape) return;
                        var ac = dlg.ActiveControl as ComboBox;
                        if (ac != null && ac.DroppedDown) return;
                        e2.Handled = true;
                        dlg.DialogResult = DialogResult.Cancel;
                        dlg.Close();
                    }
                    catch { }
                };
            }
            catch { }
            var of = ResolveDimOwner(owner);
            Form? dim = null;
            bool pushed = false;
            try
            {
                if (of != null)
                {
                    if (_dimDepth == 0) dim = OpenDim(of);
                    _dimDepth++;
                    pushed = true;
                    return dlg.ShowDialog(of);
                }
                return dlg.ShowDialog(owner);
            }
            finally
            {
                if (pushed) _dimDepth = Math.Max(0, _dimDepth - 1);
                try { dim?.Close(); dim?.Dispose(); } catch { }
            }
        }
    }
}