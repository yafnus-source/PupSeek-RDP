using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // Force-paints DataGridView column headers with the Figma palette,
    // bypassing WinForms VisualStyles entirely.
    //
    // EnableHeadersVisualStyles = false is UNRELIABLE in .NET 8 — the system
    // theme (#0078D7 blue) bleeds through despite the property being set.
    // The ONLY reliable fix is to handle CellPainting for header rows and
    // paint the background ourselves. AddressBookForm already does this and
    // its headers look correct; this helper applies the same pattern to
    // every other grid.
    public static class UiGridStyle
    {
        // v3.9.3CB — синяя тема: таблицы как в Даше-Мониторе
        // (шапка беж→синий аналог, зебра и ховер — мониторные).
        private static bool IsBlue => UiTheme.CurrentName != "Olive";
        private static readonly Color BlueHeadTop = Color.FromArgb(0xE0, 0xE7, 0xF0);
        private static readonly Color BlueHeadBot = Color.FromArgb(0xD3, 0xDC, 0xE8);
        private static readonly Color BlueHeadText = Color.FromArgb(0x20, 0x36, 0x4E);
        private static readonly Color BlueRowA = Color.FromArgb(0xFF, 0xFF, 0xFF);
        private static readonly Color BlueRowB = Color.FromArgb(0xF5, 0xF5, 0xF5);
        private static readonly Color BlueHover = Color.FromArgb(0xE9, 0xEF, 0xF6);
        // v3.9.3CC — общая синяя шапка для гридов с собственной отрисовкой
        // (HARD и др.): градиент + тёмно-синий текст, как у всех таблиц.
        public static Color HeaderTextColor => IsBlue ? BlueHeadText : UiTheme.Foreground;
        public static void PaintHeaderBg(DataGridViewCellPaintingEventArgs e, Rectangle rect)
        {
            try
            {
                if (rect.Width <= 0 || rect.Height <= 0) return;
                if (IsBlue)
                {
                    using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(rect,
                        BlueHeadTop, BlueHeadBot, 90f);
                    e.Graphics.FillRectangle(lg, rect);
                }
                else
                {
                    using var flat = new SolidBrush(Color.White);
                    e.Graphics.FillRectangle(flat, rect);
                }
            }
            catch { }
        }
        // Ховер-строка грида. Хранилище состояния — по одному слоту на
        // грид (PaintHeaders вызывается один раз на грид из ApplyUnified).
        private sealed class HoverSlot { public int Row = -1; }
        private static readonly ConditionalWeakTable<DataGridView, HoverSlot> _hover = new();

        // v4.5.0 — номер строки под курсором (-1 — курсор вне строк). Нужен
        // гридам с собственной отрисовкой ячеек (адресная книга), чтобы
        // тинтить кастомные ячейки самим — общий тинт через e.CellStyle до
        // них может не дойти при другом порядке подписок CellPainting.
        public static int HoverRowFor(DataGridView grid)
        {
            try { return _hover.TryGetValue(grid, out var s) ? s.Row : -1; }
            catch { return -1; }
        }
        // v2.1.20: header style — white bg, dark text, 4px gap.
        //   - Background: #FFFFFF (white)
        //   - Text: #1C1917 (Foreground)
        //   - 4px gap between cells (form bg shows through)
        //   - Bold Segoe UI 9pt
        // v2.1.23: also adds row hover effect — neutral warm tint matching
        // the Figma palette. Applied as a CellMouseEnter/Leave on the grid.
        public static void PaintHeaders(DataGridView grid, int padX = 12)
        {
            // v3.7.x: drag column headers to reorder in any grid, with the
            // order persisted per form (see GridColumnOrder). Plain clicks
            // still work — sorting (address book) is unaffected.
            GridColumnOrder.Wire(grid);

            grid.CellPainting += (sender, e) =>
            {
                if (sender is not DataGridView grid) return;
                if (e is null || e.RowIndex != -1 || e.ColumnIndex < 0) return;

                // Fill FULL cell with form Background (creates the 3px gap).
                // v3.9.3CK — в синей теме зазоров нет: льём низ шапки,
                // иначе белые полосы-границы между хедерами.
                using var gapBrush = new SolidBrush(IsBlue ? BlueHeadBot : UiTheme.Background);
                e.Graphics!.FillRectangle(gapBrush, e.CellBounds);

                // v2.1.21: 3px gap (2px left + 1px right = 3px between cells).
                var inner = new Rectangle(
                    e.CellBounds.X + 2, e.CellBounds.Y + 2,
                    e.CellBounds.Width - 3, e.CellBounds.Height - 4);
                if (inner.Width > 0 && inner.Height > 0)
                    PaintHeaderBg(e, inner);

                // Header text — dark, bold, left-aligned with padding.
                var headerText = grid.Columns[e.ColumnIndex].HeaderText;
                var font = new Font("Segoe UI Semibold", 9F);
                var textRect = new Rectangle(
                    inner.X + padX, inner.Y,
                    inner.Width - padX, inner.Height);
                TextRenderer.DrawText(e.Graphics, headerText, font, textRect,
                    HeaderTextColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis);

                font.Dispose();
                e.Handled = true;
            };

            // Row hover — neutral warm tint. v2.1.23 делал это через
            // cell.Style.BackColor на ВСЕХ ячейках старой+новой строки при
            // КАЖДОМ CellMouseMove: каждый сеттер Style → StyleChanged →
            // инвалидация + расшаривание строки + полный синхронный репейн.
            // На небуферизованных гридах (адресная книга) взмах мышью
            // превращался в потоп UI-потока — «тормоза как на 386».
            // v4.5.0: трекаем только номер строки (int), на смену — один
            // Invalidate, а тинт рисуется в штатном склеенном кадре через
            // e.CellStyle (в CellPainting его менять разрешено — на ячейки
            // не записывается, инвалидаций и расшаривания нет).
            var slot = _hover.GetOrCreateValue(grid);

            grid.CellMouseMove += (sender, e) =>
            {
                int row = e.RowIndex;
                if (row < 0 || row >= grid.Rows.Count) return;
                if (row == slot.Row) return;
                slot.Row = row;
                if (!grid.IsDisposed) grid.Invalidate();
            };
            grid.MouseLeave += (sender, e) =>
            {
                if (slot.Row < 0) return;
                slot.Row = -1;
                if (!grid.IsDisposed) grid.Invalidate();
            };
            // Тинт ховера — в том же проходе отрисовки. Подписано ПОЗЖЕ
            // хедерного CellPainting выше, но РАНЬШЕ обработчиков самих форм
            // (ApplyUnified вызывается в конструкторе первым), поэтому
            // кастомные ячейки, красящие фон через e.PaintBackground,
            // тинт подхватывают автоматически.
            grid.CellPainting += (sender, e) =>
            {
                if (sender is not DataGridView) return;
                if (e is null || e.RowIndex < 0 || e.RowIndex != slot.Row) return;
                e.CellStyle.BackColor = IsBlue ? BlueHover : UiTheme.HoverRow;
            };
        }

        // v3.9.3F — обязательная первая строка любой ветки кастомного
        // CellPainting с e.Handled=true («болезнь чёрных граней»):
        // 1. Заливает ВЕСЬ CellBounds (недокрашенные пиксели = чёрный мусор
        //    двойного буфера).
        // 2. Перекрывает тёмную кромку 1px, которую DataGridView красит
        //    сверху/слева handled-ячейки независимо от BorderStyle=None
        //    (доказано OpenFilesForm).
        // bg обычно = e.CellStyle.BackColor (учитывает selection/alternating).
        public static void PaintCellBase(DataGridViewCellPaintingEventArgs e, Color bg)
        {
            try
            {
                using var br = new SolidBrush(bg);
                e.Graphics.FillRectangle(br, e.CellBounds);
                e.Graphics.FillRectangle(br, e.CellBounds.X, e.CellBounds.Y, e.CellBounds.Width, 1);
                e.Graphics.FillRectangle(br, e.CellBounds.X, e.CellBounds.Y, 1, e.CellBounds.Height);
            }
            catch { }
        }

        // v3.9.3M — заливка с нахлёстом 1px вправо/вниз: убивает микрощели
        // между соседними ячейками (любой недокрашенный пиксель = тёмная
        // полоска). Вызывать ВМЕСТО прямого FillRectangle(baseBg,...).
        public static void PaintCellBaseOverlap(DataGridViewCellPaintingEventArgs e, Color bg)
        {
            try
            {
                using var br = new SolidBrush(bg);
                var r = e.CellBounds;
                e.Graphics.FillRectangle(br, r.X, r.Y, r.Width + 1, r.Height + 1);
                e.Graphics.FillRectangle(br, r.X, r.Y, r.Width, 1);
                e.Graphics.FillRectangle(br, r.X, r.Y, 1, r.Height);
            }
            catch { }
        }

        public static void ApplyUnified(DataGridView grid, int rowHeight = 36, int headerHeight = 36, bool whiteRows = false)
        {
            grid.BackgroundColor = UiTheme.Card;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = UiTheme.BorderSubtle;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.ReadOnly = true;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.RowTemplate.Height = rowHeight;
            grid.ColumnHeadersHeight = headerHeight;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.AdvancedCellBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            grid.AdvancedCellBorderStyle.Top = DataGridViewAdvancedCellBorderStyle.None;
            grid.AdvancedCellBorderStyle.Bottom = DataGridViewAdvancedCellBorderStyle.None;
            grid.AdvancedCellBorderStyle.Left = DataGridViewAdvancedCellBorderStyle.None;
            grid.AdvancedCellBorderStyle.Right = DataGridViewAdvancedCellBorderStyle.None;
            grid.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            grid.AdvancedColumnHeadersBorderStyle.Bottom = DataGridViewAdvancedCellBorderStyle.None;
            grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.White;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Foreground;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9F);
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            grid.DefaultCellStyle.Font = new Font("Segoe UI", 9F);
            grid.DefaultCellStyle.BackColor = UiTheme.RowBase;
            grid.DefaultCellStyle.ForeColor = UiTheme.Foreground;
            // v3.9.3BI — выбор #E8ECE8 (как ховер).
            grid.DefaultCellStyle.SelectionBackColor = UiTheme.AccentSoft;
            grid.DefaultCellStyle.SelectionForeColor = UiTheme.GridSelectionFore;
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            grid.AlternatingRowsDefaultCellStyle.BackColor = UiTheme.RowAlt;
            // v3.9.3BI — выбор #E8ECE8.
            grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = UiTheme.AccentSoft;
            grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = UiTheme.GridSelectionFore;
            grid.AlternatingRowsDefaultCellStyle.Font = new Font("Segoe UI", 9F);
            // v3.9.3CB — синяя тема: зебра и выбор как в Мониторе.
            // v3.9.3CG — whiteRows: книга в синей теме на белых строках.
            if (IsBlue)
            {
                grid.DefaultCellStyle.BackColor = (whiteRows ? Color.White : BlueRowA);
                grid.AlternatingRowsDefaultCellStyle.BackColor = (whiteRows ? Color.FromArgb(0xF5, 0xF5, 0xF5) : BlueRowB);
                grid.DefaultCellStyle.SelectionBackColor = BlueHover;
                grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = BlueHover;
                grid.DefaultCellStyle.SelectionForeColor = BlueHeadText;
                grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = BlueHeadText;
            }
            PaintHeaders(grid, 12);
        }

        // v3.9.5 — стиль таблицы из «Открытых файлов» (OpenFilesForm):
        // ApplyUnified + кремовые строки #FDFBF7 + скруглённые углы 7px +
        // системный скролл ShadowScroll. Применять к гридам новых окон,
        // чтобы они выглядели как в образце.
        public static void ApplyCardGrid(DataGridView grid, int rowHeight = 36, int headerHeight = 36)
        {
            ApplyUnified(grid, rowHeight, headerHeight);
            grid.DefaultCellStyle.BackColor = Color.FromArgb(0xFD, 0xFB, 0xF7);
            RoundCorners(grid, 7);
            try { ShadowRDP.Controls.ShadowScroll.AttachGrid(grid); } catch { }
        }

        // То же + присоединение к headerless-карточке (UiModal.Card с
        // withHeader:false) с равномерным отступом 14px — как FitCardChild
        // в OpenFilesForm.
        public static void ApplyCardGrid(DataGridView grid, Panel card, int rowHeight = 36, int headerHeight = 36)
        {
            ApplyCardGrid(grid, rowHeight, headerHeight);
            card.Controls.Add(grid);
            void Fit() => grid.Bounds = new Rectangle(14, 14,
                Math.Max(50, card.ClientSize.Width - 28),
                Math.Max(50, card.ClientSize.Height - 28));
            card.Resize += (s, e) => { Fit(); try { card.Invalidate(true); } catch { } };
            grid.Resize += (s, e) => { try { grid.Invalidate(); } catch { } };
            grid.ColumnWidthChanged += (s, e) => { try { grid.Invalidate(); } catch { } };
            Fit();
        }

        // Скруглённые углы через Region (копия OpenFilesForm.RoundCorners).
        private static void RoundCorners(Control c, int radius)
        {
            void Update()
            {
                if (c.IsDisposed || c.Width <= 0 || c.Height <= 0) return;
                c.Region?.Dispose();
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                var r = new Rectangle(0, 0, c.Width, c.Height);
                int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                c.Region = new Region(path);
            }
            c.Resize += (s, e) => Update();
            Update();
        }
    }
}
