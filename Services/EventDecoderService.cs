using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ShadowRDP.Helpers;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    public static class EventDecoderService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        private static string OllamaBase => $"http://{Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "localhost:11434"}";

        public static async Task<string> DecodeAsync(DashboardEvent ev)
        {
            if (!string.IsNullOrWhiteSpace(ev.Decoded)) return ev.Decoded;

            // 1) Try Ollama
            if (await IsOllamaAvailableAsync())
            {
                try
                {
                    var prompt = $@"Ты — эксперт по Windows Event Log. Событие: EventID {ev.EventId}, Источник: {ev.Source}, Уровень: {ev.Level}, ПК: {ev.Computer}. Сообщение: {ev.Message}.

Ответь подробно на русском языке (~300 слов), по пунктам:
1) Что означает это событие (простым языком, с конкретикой по этому EventID)
2) Возможные причины (перечисли 3-4 варианта)
3) Что делать — пошаговые действия по устранению (с примерами команд/настроек)
4) Критичность (Low / Medium / High) и почему.

Не начинай ответ словами «Окончательный ответ», «Ответ:» или «Final answer» — сразу переходи к пункту 1. Отвечай только по делу, без приветствий.";
                    var payload = new
                    {
                        model = "llama3.2",
                        messages = new[] { new { role = "user", content = prompt } },
                        stream = false,
                        options = new { num_predict = 800, temperature = 0.3 }
                    };
                    var json = JsonSerializer.Serialize(payload);
                    var resp = await _http.PostAsync($"{OllamaBase}/api/chat", new StringContent(json, Encoding.UTF8, "application/json"));
                    if (resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                        {
                            var text = StripLeader(content.GetString()?.Trim());
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                ev.Decoded = text;
                                PersistDecoded(ev);
                                return text;
                            }
                        }
                    }
                }
                catch (Exception ex) { try { Logger.Warn($"Ollama decode failed: {ex.Message}"); } catch { } }
            }

            // 2) Fallback DB
            if (Helpers.EventFallbackDb.TryGet(ev.EventId, ev.Source, out var fb))
            {
                ev.Decoded = fb;
                PersistDecoded(ev);
                return fb;
            }

            // 3) Generic fallback
            string generic = $"Событие {ev.EventId} от {ev.Source} ({ev.Level}) на {ev.Computer}: {ev.Message}. Установите Ollama (кнопка «Установка ИИ для расшифровки») для подробной расшифровки ИИ.";
            ev.Decoded = generic;
            PersistDecoded(ev);
            return generic;
        }

        // v4.0.0.1 — ИИ-анализ журнала одной машины (см. Helpers.AiEventAnalyzer):
        // на вход агрегаты «ID | источник | уровень | сколько раз | последнее
        // время | образец текста», на выход — сводный разбор «что не работает,
        // почему, что делать». Кэша и персиста нет — анализ всегда свежий.
        public static async Task<string?> AnalyzeComputerAsync(string computer, IReadOnlyList<AiEventAgg> aggs)
        {
            if (!await IsOllamaAvailableAsync()) return null;

            var sb = new StringBuilder();
            sb.AppendLine($"Ты — эксперт по Windows Event Log и системному администрированию. Проанализируй журнал событий компьютера {computer}.");
            sb.AppendLine($"Ниже {aggs.Count} уникальных событий (агрегировано по EventID и источнику). Формат строки: EventID | Источник | Уровень | сколько раз | последнее время | пример текста.");
            sb.AppendLine();
            foreach (var a in aggs)
                sb.AppendLine($"- {a.Id} | {a.Source} | {a.Level} | {a.Count}x | {a.Last} | {a.Sample}");
            sb.AppendLine();
            sb.AppendLine(@"Ответь подробно на русском языке (~400 слов), по пунктам:
1) Что не работает или работает нестабильно — какие проблемы видно по совокупности этих событий (простым языком, с конкретикой по EventID).
2) Самые вероятные причины (перечисли 3-4).
3) Что делать — пошаговые действия по устранению, с примерами команд и настроек.
4) Критичность для машины: Low / Medium / High и почему.
5) Что ещё проверить, чтобы подтвердить диагноз.

Не начинай ответ словами «Окончательный ответ», «Ответ:» или «Final answer» — сразу переходи к пункту 1. Отвечай только по делу, без приветствий.");

            try
            {
                var payload = new
                {
                    model = "llama3.2",
                    messages = new[] { new { role = "user", content = sb.ToString() } },
                    stream = false,
                    options = new { num_predict = 1200, temperature = 0.3 }
                };
                var json = JsonSerializer.Serialize(payload);
                var resp = await _http.PostAsync($"{OllamaBase}/api/chat", new StringContent(json, Encoding.UTF8, "application/json"));
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                    {
                        return StripLeader(content.GetString()?.Trim());
                    }
                }
            }
            catch (Exception ex) { try { Logger.Warn($"Ollama analyze failed: {ex.Message}"); } catch { } }
            return null;
        }

        public static async Task<bool> IsOllamaAvailableAsync()
        {
            try
            {
                using var checkHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var resp = await checkHttp.GetAsync($"{OllamaBase}/api/tags");
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private static void PersistDecoded(DashboardEvent ev)
        {
            try
            {
                var all = EventDashboardStore.LoadEvents();
                var idx = all.FindIndex(e => e.Computer == ev.Computer && e.EventId == ev.EventId && e.Time == ev.Time);
                if (idx >= 0) { all[idx].Decoded = ev.Decoded; EventDashboardStore.SaveEvents(all); }
            }
            catch { }
        }

        private static string StripLeader(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            string[] markers = { "Окончательный ответ:", "Окончательный ответ :", "Final answer:", "Final Answer:", "Ответ:" };
            foreach (var m in markers)
            {
                if (text.StartsWith(m, StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Substring(m.Length).TrimStart(':', ' ', '\n', '\r');
                    break;
                }
            }
            return text.Trim();
        }
    }
}
