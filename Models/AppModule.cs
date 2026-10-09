using System.Windows.Forms;

namespace ShadowRDP.Models
{
    // v4.7 — встраиваемый модуль: опциональная фича, которая ставится
    // по желанию через «Инструменты → Модули». Код живёт в exe (single-file),
    // «установка» = включение тумблера в менеджере: кнопка сайдбара, пункт
    // трея и фоновые сервисы появляются/стартуют только у включённого модуля.
    // Первый модуль — "todo" (TODO/шня); дальше можно выносить другие фичи.
    public abstract class AppModule
    {
        // Стабильный строковый идентификатор (ключ в modules.json).
        public abstract string Id { get; }
        // Публичное имя («TODO/шня»).
        public abstract string Name { get; }
        // Короткое описание для менеджера модулей.
        public virtual string Description => "";
        // Версия модуля (показывается в менеджере).
        public virtual string Version => "1.0";
        // Значение, когда записи в modules.json нет. По умолчанию модуль
        // «не установлен». Отключение НЕ удаляет данные (todo.json на месте).
        public virtual bool DefaultEnabled => false;

        // Показывать ли кнопку в сайдбаре (вкладка модуля).
        public virtual bool ShowInSidebar => true;
        // Ключ иконки для кнопки сайдбара (как в UiButtons.MakeFilled).
        public virtual string IconKey => "box";
        // Текст пункта в трее-меню. Пусто = пункт не показывать.
        public virtual string TrayLabel => "";
        // Текст кнопки сайдбара. Пусто = Name (или Name + бейдж).
        public virtual string SidebarText() => Name;

        // Жизненный цикл: вызываются при включении/выключении модуля
        // (старт приложения и тумблер в менеджере).
        public virtual void OnEnable() { }
        public virtual void OnDisable() { }

        // Создать внедряемое представление (Form с TopLevel=false,
        // FormBorderStyle=None, Dock=Fill) для хост-панели главной формы.
        public virtual Control? CreateTabContent() => null;
    }
}
