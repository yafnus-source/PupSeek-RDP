using System;
using System.Text.Json.Serialization;

namespace ShadowRDP.Models
{
    public enum TodoStatus
    {
        Active = 0,
        Done = 1,
        Postponed = 2,
        Cancelled = 3
    }

    public class TodoItem
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("done")]
        public bool Done { get; set; }

        [JsonPropertyName("status")]
        public TodoStatus Status { get; set; } = TodoStatus.Active;

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [JsonPropertyName("dueAt")]
        public DateTime? DueAt { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; } = "manual"; // manual | import | calendar

        [JsonPropertyName("notified")]
        public bool Notified { get; set; }

        // v3.9.3CZ — оповещение: за сколько минут до срока напомнить
        // (0 = в срок, без упреждения). Повтор — пере-напоминать,
        // пока задача не выполнена.
        [JsonPropertyName("remindMinutes")]
        public int RemindMinutes { get; set; }

        [JsonPropertyName("repeatRemind")]
        public bool RepeatRemind { get; set; }

        [JsonPropertyName("lastRemindedAt")]
        public DateTime? LastRemindedAt { get; set; }

        // UID события из ICS (для дедупликации повторного импорта календаря)
        [JsonPropertyName("calendarUid")]
        public string? CalendarUid { get; set; }

        [JsonPropertyName("repeat")]
        public TodoRepeat Repeat { get; set; } = TodoRepeat.None;

        // Weekly: selected weekdays, 0=Sunday .. 6=Saturday
        [JsonPropertyName("repeatDays")]
        public System.Collections.Generic.List<int> RepeatDays { get; set; } = new();

        // Monthly: day of month 1..31 (31 = last day of month)
        [JsonPropertyName("repeatDay")]
        public int RepeatDayOfMonth { get; set; }
    }

    public enum TodoRepeat
    {
        None = 0,
        Daily = 1,
        Weekly = 2,
        Monthly = 3
    }

    public class TodoSyncSettings
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        // free: local + optional cloud polling. Empty = only local file.
        [JsonPropertyName("apiUrl")]
        public string ApiUrl { get; set; } = "";

        [JsonPropertyName("apiToken")]
        public string ApiToken { get; set; } = "";

        [JsonPropertyName("pollIntervalSeconds")]
        public int PollIntervalSeconds { get; set; } = 15;

        // fallback watcher for instant local updates (e.g. adb push, file copy)
        [JsonPropertyName("useFileWatcher")]
        public bool UseFileWatcher { get; set; } = true;

        // v4.9.4 — автосинк календаря по URL (webcal/https .ics).
        // Пустой URL = фича выключена. Скачиваем раз в CalendarSyncMinutes,
        // дедуп по calendarUid, новые события — в список как source "calendar".
        [JsonPropertyName("calendarUrl")]
        public string CalendarUrl { get; set; } = "";

        [JsonPropertyName("calendarSyncMinutes")]
        public int CalendarSyncMinutes { get; set; } = 15;

        [JsonPropertyName("calendarLastSync")]
        public DateTime? CalendarLastSync { get; set; }

        // Tombstones удалённых задач: Merge пропускает эти Id, чтобы опрос
        // не воскрешал удалённое с сервера (удаление идёт и локально, и на сервер).
        [JsonPropertyName("deletedIds")]
        public System.Collections.Generic.List<string> DeletedIds { get; set; } = new();
    }
}
