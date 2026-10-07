using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Models;

namespace ShadowRDP.Services
{
    // v4.7 — первый встраиваемый модуль: TODO.шная. По умолчанию «не установлен»
    // (кнопки в сайдбаре/трее нет, сервисы не стартуют), пока не включён в
    // «Инструменты → Модули». Отключение — это установка тумблера, данные
    // todo.json сохраняются и вернутся при повторном включении.
    public sealed class TodoModule : AppModule
    {
        public override string Id => "todo";
        public override string Name => "TODO.шная";
        public override string Description => "Личные задачи: список, сроки, повторы, напоминания из трея. Данные: todo.json.";
        public override string Version => "1.0";
        public override bool DefaultEnabled => false;
        public override bool ShowInSidebar => true;
        public override string IconKey => "tasks";
        public override string TrayLabel => "TODO.шная";

        public override string SidebarText()
        {
            try
            {
                var count = TodoStore.Load().Count(x => !x.Done);
                return count > 0 ? $"TODO.шная  • {count}" : Name;
            }
            catch { return Name; }
        }

        public override void OnEnable()
        {
            // v3.9.3CZ — телефонный синк (TodoSyncService) вырезан.
            try { TodoReminderService.Start(); } catch { }
            try { CalendarSyncService.Start(); } catch { }
        }

        public override void OnDisable()
        {
            try { TodoReminderService.Stop(); } catch { }
            try { CalendarSyncService.Stop(); } catch { }
        }

        public override Control? CreateTabContent()
        {
            var f = new Forms.TodoForm
            {
                TopLevel = false,
                FormBorderStyle = FormBorderStyle.None,
                Dock = DockStyle.Fill,
                Visible = true
            };
            try { f.Reload(); } catch { }
            return f;
        }
    }
}
