using System.Drawing;
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
                using var gapBrush = new SolidBrush(UiTheme.Background);
                e.Graphics!.FillRectangle(gapBrush, e.CellBounds);

                // v2.1.21: 3px gap (2px left + 1px right = 3px between cells).
                var inner = new Rectangle(
                    e.CellBounds.X + 2, e.CellBounds.Y + 2,
                    e.CellBounds.Width - 3, e.CellBounds.Height - 4);
                using var bgBrush = new SolidBrush(Color.White);
                e.Graphics.FillRectangle(bgBrush, inner);

                // Header text — dark, bold, left-aligned with padding.
                var headerText = grid.Columns[e.ColumnIndex].HeaderText;
                var font = new Font("Segoe UI Semibold", 9F);
                var textRect = new Rectangle(
                    inner.X + padX, inner.Y,
                    inner.Width - padX, inner.Height);
                TextRenderer.DrawText(e.Graphics, headerText, font, textRect,
                    UiTheme.Foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis);

                font.Dispose();
                e.Handled = true;
            };

            // Row hover — neutral warm tint. DataGridView has no per-row hover
            // style, and per-cell CellMouseEnter/Leave restyles every cell on
            // every mouse move (constant repaints). Instead track the single
            // row under the cursor and only restyle when it actually changes.
            int hoverRow = -1;

            void HighlightRow(int rowIndex)
            {
                if (rowIndex < 0 || rowIndex >= grid.Rows.Count) return;
                foreach (DataGridViewCell cell in grid.Rows[rowIndex].Cells)
                {
                    if (cell.Style.BackColor == UiTheme.HoverRow) continue;
                    cell.Tag = cell.Style.BackColor;
                    cell.Style.BackColor = UiTheme.HoverRow;
                    cell.Style.SelectionBackColor = UiTheme.AccentSoft;
                }
            }

            void RestoreRow(int rowIndex)
            {
                if (rowIndex < 0 || rowIndex >= grid.Rows.Count) return;
                foreach (DataGridViewCell cell in grid.Rows[rowIndex].Cells)
                {
                    var orig = cell.Tag is Color c ? c : UiTheme.Card;
                    cell.Style.BackColor = orig;
                    cell.Style.SelectionBackColor = UiTheme.AccentSoft;
                }
            }

            grid.CellMouseMove += (sender, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex == hoverRow) return;
                if (hoverRow >= 0) RestoreRow(hoverRow);
                hoverRow = e.RowIndex;
                HighlightRow(hoverRow);
            };
            grid.MouseLeave += (sender, e) =>
            {
                if (hoverRow < 0) return;
                RestoreRow(hoverRow);
                hoverRow = -1;
            };
        }
    }
}
