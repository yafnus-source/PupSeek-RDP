namespace ShadowRDP.Models
{
    // v3.9.4 — сниппет SSH-команды: именованная команда для отправки
    // в терминал одним кликом. Group — необязательная группа в меню.
    public class SshSnippet
    {
        public string Name { get; set; } = "";
        public string Group { get; set; } = "";
        public string Command { get; set; } = "";
    }
}
