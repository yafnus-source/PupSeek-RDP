namespace ShadowRDP.Models
{
    // v3.9.4 — сниппет SSH-команды: именованная команда для отправки
    // в терминал одним кликом. Group — необязательная группа в меню.
    // v3.9.5 — Description: краткое пояснение (колонка «Пояснение» в пикере).
    public class SshSnippet
    {
        public string Name { get; set; } = "";
        public string Group { get; set; } = "";
        public string Command { get; set; } = "";
        public string Description { get; set; } = "";
    }
}
