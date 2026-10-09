using System;
using System.Drawing;
using System.Windows.Forms;
using QRCoder;

namespace ShadowRDP.Helpers
{
    // QR-привязка для TODO/шня — бесплатно, без токена по умолчанию.
    // Показывает QR с payload: todo://pair?url=<url>&token=<token>  (если token пустой — без него)
    // Телефон сканирует QR и автоматом заполняет ApiUrl+token.
    public static class TodoQrDialog
    {
        public static void Show(IWin32Window owner, string url, string token)
        {
            url = (url ?? "").Trim().TrimEnd('/');
            if (!url.EndsWith("/")) url += "/";
            token = (token ?? "").Trim();

            string payload = string.IsNullOrWhiteSpace(token)
                ? $"todo://pair?url={Uri.EscapeDataString(url)}"
                : $"todo://pair?url={Uri.EscapeDataString(url)}&token={Uri.EscapeDataString(token)}";

            // Генерим QR
            Bitmap qr = null!;
            try
            {
                using var gen = new QRCodeGenerator();
                var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
                using var qrCode = new QRCode(data);
                qr = qrCode.GetGraphic(20, Color.FromArgb(30, 58, 95), Color.White, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"QR не сгенерился: {ex.Message}", "TODO/шня", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var dlg = new Form
            {
                Text = "Подключить телефон — QR",
                ClientSize = new Size(460, 560),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = UiTheme.DialogBackground,
                Font = new Font("Segoe UI", 9F)
            };

            var lblTitle = new Label
            {
                Text = "Отсканируй QR в приложении TODO/шня",
                Location = new Point(16, 14),
                Size = new Size(428, 20),
                Font = new Font("Segoe UI Semibold", 10F),
                ForeColor = UiTheme.Foreground
            };
            dlg.Controls.Add(lblTitle);

            var lblHint = new Label
            {
                Text = "На телефоне: открой TODO/шня → ⋮ → Сканировать QR. Токен не нужен — оставь $TODO_TOKEN пустым на сервере.",
                Location = new Point(16, 38),
                Size = new Size(428, 36),
                ForeColor = UiTheme.MutedForeground,
                Font = new Font("Segoe UI", 8.5F)
            };
            dlg.Controls.Add(lblHint);

            var pic = new PictureBox
            {
                Image = qr,
                Location = new Point(80, 80),
                Size = new Size(300, 300),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            dlg.Controls.Add(pic);

            var lblUrl = new Label
            {
                Text = $"URL: {url}",
                Location = new Point(16, 390),
                Size = new Size(428, 18),
                ForeColor = UiTheme.Foreground,
                Font = new Font("Segoe UI", 8.5F)
            };
            dlg.Controls.Add(lblUrl);

            var lblToken = new Label
            {
                Text = string.IsNullOrWhiteSpace(token) ? "Токен: — (без токена, верно для pupseek.ru)" : $"Токен: {token}",
                Location = new Point(16, 410),
                Size = new Size(428, 18),
                ForeColor = UiTheme.MutedForeground,
                Font = new Font("Segoe UI", 8.5F)
            };
            dlg.Controls.Add(lblToken);

            var lblPayload = new Label
            {
                Text = $"Данные QR: {payload}",
                Location = new Point(16, 432),
                Size = new Size(428, 36),
                ForeColor = Color.Gray,
                Font = new Font("Consolas", 7F)
            };
            dlg.Controls.Add(lblPayload);

            var btnCopyUrl = UiButtons.MakeCompactFilled("Копировать URL", 130);
            btnCopyUrl.Location = new Point(16, 480);
            btnCopyUrl.Click += (s, e) => { try { Clipboard.SetText(url); } catch { } };
            dlg.Controls.Add(btnCopyUrl);

            var btnCopyToken = UiButtons.MakeCompactFilled("Копировать токен", 130);
            btnCopyToken.Location = new Point(156, 480);
            btnCopyToken.Click += (s, e) => { try { Clipboard.SetText(token); } catch { } };
            dlg.Controls.Add(btnCopyToken);

            var btnCopyPayload = UiButtons.MakeCompactFilled("Копировать QR-текст", 150);
            btnCopyPayload.Location = new Point(296, 480);
            btnCopyPayload.Click += (s, e) => { try { Clipboard.SetText(payload); } catch { } };
            dlg.Controls.Add(btnCopyPayload);

            var btnClose = new Button
            {
                Text = "Закрыть",
                DialogResult = DialogResult.OK,
                Location = new Point(190, 520),
                Size = new Size(80, 28),
                FlatStyle = FlatStyle.Flat
            };
            dlg.Controls.Add(btnClose);
            dlg.AcceptButton = btnClose;
            dlg.KeyPreview = true;
            dlg.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; dlg.Close(); } };

            UiModal.ShowDimmed(owner, dlg);
            try { qr.Dispose(); } catch { }
        }

        public static string GenerateToken(int len = 8)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var rnd = new Random();
            var s = "";
            for (int i = 0; i < len; i++) s += chars[rnd.Next(chars.Length)];
            return s;
        }
    }
}
