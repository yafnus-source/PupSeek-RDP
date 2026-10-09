using System;
using System.Drawing;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v3.9.5 — единый стиль выпадающих списков во всей программе.
    // Плоская рамка (FlatStyle.Flat), цвета темы, белая/карточная
    // подложка и owner-draw для DropDownList: текст с отступом 10px,
    // выбранная позиция — тон AccentSoft + акцентная полоска 3px слева.
    // Пока без ховера внутри попапа: выпадающий список — отдельное
    // системное окно, WinForms не отдаёт номер строки под курсором.
    //
    // Для редактируемых комбо (ComboBoxStyle.DropDown) owner-draw не
    // включаем: текстовая часть с кареткой/автодополнением должна
    // рисоваться системой, иначе ломается ввод.
    public static class UiCombo
    {
        // back — подложка закрытого бокса и пунктов списка (по умолчанию
        // карточная белая; UiModal.Field передаёт чистый White).
        public static void Apply(ComboBox cb, Color? back = null)
        {
            if (cb == null) return;
            try
            {
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = back ?? UiTheme.DialogCard;
                cb.ForeColor = UiTheme.Foreground;
                // Списков с десятками пунктов (группы сниппетов, сессии)
                // больше видно за раз; при малом числе пунктов попап
                // всё равно сжимается под содержимое (IntegralHeight).
                if (cb.IntegralHeight) cb.DropDownHeight = Math.Max(cb.DropDownHeight, 300);

                if (cb.DropDownStyle != ComboBoxStyle.DropDownList) return;
                cb.DrawItem -= OnDrawItem;
                cb.DrawItem += OnDrawItem;
                if (cb.DrawMode != DrawMode.OwnerDrawFixed)
                    cb.DrawMode = DrawMode.OwnerDrawFixed;
            }
            catch { }
        }

        private static void OnDrawItem(object? sender, DrawItemEventArgs e)
        {
            try
            {
                var cb = (ComboBox)sender!;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                bool disabled = !cb.Enabled || (e.State & DrawItemState.Disabled) != 0;

                var back = sel ? UiTheme.AccentSoft
                    : (cb.BackColor.A == 0 ? UiTheme.DialogCard : cb.BackColor);
                using (var bg = new SolidBrush(back))
                    e.Graphics.FillRectangle(bg, e.Bounds);

                if (sel)
                {
                    using var bar = new SolidBrush(UiTheme.Accent);
                    e.Graphics.FillRectangle(bar,
                        e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
                }

                string text = e.Index >= 0 && e.Index < cb.Items.Count
                    ? cb.Items[e.Index]?.ToString() ?? "" : "";
                if (text.Length > 0)
                {
                    var textRect = new Rectangle(
                        e.Bounds.X + 10, e.Bounds.Y,
                        Math.Max(10, e.Bounds.Width - 14), e.Bounds.Height);
                    TextRenderer.DrawText(e.Graphics, text, cb.Font, textRect,
                        disabled ? UiTheme.MutedForeground : UiTheme.Foreground,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                }
            }
            catch { }
        }
    }
}
