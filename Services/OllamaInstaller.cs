using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShadowRDP.Services
{
    public static class OllamaInstaller
    {
        public static async Task<bool> IsInstalledAsync() => await EventDecoderService.IsOllamaAvailableAsync();

        public static void ShowInstallGuide(IWin32Window owner)
        {
            var msg = "Для расшифровки событий ИИ установите Ollama:\n\n" +
                      "1. Скачайте с https://ollama.com/download/OllamaSetup.exe\n" +
                      "2. Установите (Next → Install)\n" +
                      "3. Откройте PowerShell и выполните:\n" +
                      "   ollama pull llama3.2\n" +
                      "4. Ollama запустится автоматически (http://localhost:11434)\n" +
                      "5. Вернитесь в дашборд — кнопка «Расшифровать» заработает\n\n" +
                      "Модель llama3.2 (2 ГБ) — достаточно для расшифровки.\n" +
                      "Открыть страницу загрузки сейчас?";
            if (MessageBox.Show(owner, msg, "Установка ИИ для расшифровки", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo { FileName = "https://ollama.com/download", UseShellExecute = true }); } catch { }
            }
        }

        public static async Task<bool> TryPullModelAsync(IWin32Window owner)
        {
            try
            {
                if (!await IsInstalledAsync())
                {
                    ShowInstallGuide(owner);
                    return false;
                }
                // Try pull via API
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var payload = "{\"name\":\"llama3.2\"}";
                var resp = await http.PostAsync("http://localhost:11434/api/pull", new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex) { MessageBox.Show(owner, $"Не удалось: {ex.Message}", "Ollama", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        }
    }
}
