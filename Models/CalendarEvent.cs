using System;

namespace ShadowRDP.Models
{
    // Парсинг события из ICS (календарь Outlook/Google/Exchange).
    // Только то, что нужно для импорта в ToDo.шную.
    public class CalendarEvent
    {
        // UID из ICS — ключ дедупликации при повторном импорте.
        public string Uid { get; set; } = "";

        // SUMMARY — тема события («ZOOM 14-00 Михайлюк»).
        public string Summary { get; set; } = "";

        // DTSTART, приведённый к локальному времени.
        public DateTime Start { get; set; }

        // DTEND (опционально). Для целодневных — конец дня.
        public DateTime? End { get; set; }

        // VALUE=DATE — целодневное событие (без времени).
        public bool AllDay { get; set; }

        public string Location { get; set; } = "";

        public string Description { get; set; } = "";
    }
}