using System.Drawing;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v2.2.26 — Small "EN"/"RU" badge that shows the current keyboard
    // layout. Updated on InputLanguageChanged. Useful in password dialogs
    // where the user needs to know if they're typing in Latin or Cyrillic.
    public static class LanguageIndicator
    {
        public static Label Create(Form form, Point location)
        {
            var lbl = new Label
            {
                Text = CurrentLang(),
                Location = location,
                AutoSize = true,
                Font = new Font("Consolas", 11F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = ShadowRDP.Helpers.UiTheme.HeaderBar,
                Padding = new Padding(10, 4, 10, 4),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Default
            };
            form.InputLanguageChanged += (s, e) =>
            {
                lbl.Text = CurrentLang();
            };
            return lbl;
        }

        private static string CurrentLang()
        {
            try
            {
                return InputLanguage.CurrentInputLanguage.Culture
                    .TwoLetterISOLanguageName.ToUpper();
            }
            catch { return "??"; }
        }
    }
}
