using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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
                    var prompt = $@"Проанализируй Windows-событие и дай ответ на русском языке по 4 разделам.

EventID: {ev.EventId}
Источник: {ev.Source}
Уровень: {ev.Level}
Компьютер: {ev.Computer}
Сообщение: {ev.Message}

Формат ответа:
1. Что означает — кратко (1-2 предложения)
2. Возможные причины — список
3. Что делать — конкретные шаги
4. Критичность — Low / Medium / High";
                    var payload = new
                    {
                        model = "llama3.2",
                        messages = new[] { new { role = "user", content = prompt } },
                        stream = false
                    };
                    var json = JsonSerializer.Serialize(payload);
                    var resp = await _http.PostAsync($"{OllamaBase}/api/chat", new StringContent(json, Encoding.UTF8, "application/json"));
                    if (resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                        {
                            var text = content.GetString()?.Trim();
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

        public static async Task<bool> IsOllamaAvailableAsync()
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                var resp = await _http.GetAsync($"{OllamaBase}/api/tags", cts.Token);
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
    }
}
