using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    // v4.0.0.1 — ИИ-помощник по событиям одной машины.
    //
    // Собирает агрегаты (EventID | источник | уровень | сколько раз |
    // последнее время | образец текста) и отправляет их в локальную Ollama
    // через EventDecoderService.AnalyzeComputerAsync.
    //
    // Один вход — RunAsync: гейт на модуль "ai", при отсутствии Ollama —
    // OllamaInstaller.ShowInstallGuide, ответ показывается в AiAnswerForm.
    //
    // Источники агрегатов:
    //   - EventLogForm: строки грида, очищенные/отфильтрованные пользователем
    //     (CollectFromGrid)；
    //   - ПКМ книги: дефолтная выборка Critical/Error за 48 часов по логам
    //     System + Application (CollectDefaultAsync).
    public sealed record AiEventAgg(int Id, string Source, string Level, int Count, string Last, string Sample);

    public static class AiEventAnalyzer
    {
        // Полный прогон: гейт → выборка уже сделана вызывающим → Ollama → ответ.
        public static async Task RunAsync(IWin32Window owner, string computer, string scopeText,
            List<AiEventAgg> aggs)
        {
            if (aggs == null || aggs.Count == 0)
            {
                MessageBox.Show(owner,
                    $"За выбранную выборку Error/Critical-событий на {computer} не найдено — по журналу машина выглядит здоровой.",
                    "ИИ-анализ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!await Services.EventDecoderService.IsOllamaAvailableAsync())
            {
                Services.OllamaInstaller.ShowInstallGuide(owner);
                return;
            }

            int total = aggs.Sum(a => a.Count);
            string answer;
            try
            {
                answer = await Services.EventDecoderService.AnalyzeComputerAsync(computer, aggs);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, ex.Message, "ИИ-анализ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(answer))
            {
                MessageBox.Show(owner, "ИИ не вернул ответ (Ollama занят или модель не готова). Попробуйте ещё раз.",
                    "ИИ-анализ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var dlg = new Forms.AiAnswerForm(
                $"ИИ-анализ событий — {computer}",
                $"{scopeText}  •  {total} событий, {aggs.Count} уникальных",
                answer);
            UiModal.ShowDimmed(owner, dlg);
        }

        // Гейт + уведомление, если кнопка нажата при пустом списке — совмещено в RunAsync.
        public static bool Gate(IWin32Window owner) => ModuleGate.Require(owner, "ai");

        // Дефолтная выборка из ПКМ книги: System + Application, Error, 48 часов.
        public static async Task<List<AiEventAgg>> CollectDefaultAsync(string computer)
        {
            var since = ManagementDateTimeConverter.ToDmtfDateTime(DateTime.Now.AddHours(-48));
            var wql = "SELECT TimeGenerated, SourceName, EventCode, Type, Message FROM Win32_NTLogEvent " +
                      $"WHERE (LogFile='System' OR LogFile='Application') AND Type='Error' AND TimeGenerated > '{since}'";
            return await Task.Run(() =>
            {
                var raw = new List<(int id, string source, string level, DateTime time, string msg)>();
                try
                {
                    var scope = new ManagementScope(new ManagementPath($"\\\\{computer}\\root\\cimv2"),
                        new ConnectionOptions { Timeout = TimeSpan.FromSeconds(8), Authentication = AuthenticationLevel.PacketPrivacy });
                    scope.Connect();
                    using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
                    searcher.Options.ReturnImmediately = false;
                    using var results = searcher.Get();
                    foreach (ManagementObject mo in results)
                    {
                        try
                        {
                            raw.Add((
                                Convert.ToInt32(mo["EventCode"]),
                                mo["SourceName"]?.ToString() ?? "",
                                mo["Type"]?.ToString() ?? "",
                                ManagementDateTimeConverter.ToDateTime(mo["TimeGenerated"]?.ToString() ?? ""),
                                mo["Message"]?.ToString()?.Trim() ?? ""));
                        }
                        finally { mo.Dispose(); }
                    }
                }
                catch (Exception)
                {
                    throw; // недоступность WMI видна в ПКМ-пункте как честная ошибка
                }
                return Aggregate(raw);
            });
        }

        // Агрегация плоского списка событий: группировка по ID+источник+уровень,
        // счётчик, последнее время, образец сообщения (обрезка 180 симв.).
        public static List<AiEventAgg> Aggregate(IEnumerable<(int id, string source, string level, DateTime time, string msg)> raw)
        {
            return raw.GroupBy(r => (r.id, r.source, r.level))
                .Select(g =>
                {
                    var last = g.OrderByDescending(x => x.time).First();
                    string msg = last.msg.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                    if (msg.Length > 180) msg = msg.Substring(0, 180) + "…";
                    return new AiEventAgg(g.Key.id, g.Key.source, g.Key.level, g.Count(),
                        last.time.ToString("dd.MM HH:mm"), msg);
                })
                .OrderByDescending(a => a.Count)
                .ToList();
        }
    }
}
