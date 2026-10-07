using System;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // Надёжный выход по Esc из модальных окон. В WinForms иногда native-контролы
    // (UpDown-части DateTimePicker/NumericUpDown, даты) проглатывают Escape раньше,
    // чем срабатывают CancelButton/KeyPreview формы. Эти классы перехватывают Esc
    // прямо внутри контрола и закрывают родительскую модальную форму.
    public class EscCloseForm : Form
    {
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }
    }

    public class EscDateTimePicker : DateTimePicker
    {
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                CloseOnEsc();
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                CloseOnEsc();
                return;
            }
            base.OnKeyDown(e);
        }

        private void CloseOnEsc()
        {
            var f = FindForm();
            if (f != null) f.DialogResult = DialogResult.Cancel;
        }
    }

    public class EscNumericUpDown : NumericUpDown
    {
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                CloseOnEsc();
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                CloseOnEsc();
                return;
            }
            base.OnKeyDown(e);
        }

        private void CloseOnEsc()
        {
            var f = FindForm();
            if (f != null) f.DialogResult = DialogResult.Cancel;
        }
    }
}