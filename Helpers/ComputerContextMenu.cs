using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Forms;
using ShadowRDP.Services;

namespace ShadowRDP.Helpers
{
    // Builds the right-click context menu for a computer. Used by both
    // MainForm (sessions grid) and AddressBookForm (entries grid) so they
    // stay in sync. Every item has an icon (MenuIcons.Get).
    //
    // Layout:
    //   [Sessions grid only] Добавить в адресную книгу
    //   ── session items ──
    //   Shadow / End session
    //   [Address book only] Shadow (auto-resolves session via qwinsta)
    //   Отправить сообщение...
    //   ── copy submenu ──
    //   Скопировать → (Имя компьютера | IP-адрес(а))
    //   ── info ──
    //   Информация о компьютере...
    //   Computer Management (MMC)
    //   Службы (services.msc)
    //   Просмотр событий (eventvwr)
    //   Диспетчер задач...
    //   ── domain ──
    //   ДОМЕН → (Показать ФИО | Управление службами | Локальные админы |
    //             Group Policy | Поиск где залогинен | Открытые файлы)
    //   ── power ──
    //   Перезагрузить компьютер
    //   ── ping/diag ──
    //   Пинг...
    //   Пинг в cmd (4 раза)
    //   Открыть диск C$
    //   События (Event Log)...
    //   [Address book only] Удалить
    public static class ComputerContextMenu
    {
        public static ContextMenuStrip Build(
            IWin32Window owner,
            Func<string?> computerResolver,
            Func<int>? sessionResolver = null,
            Func<string?>? userResolver = null,
            Action? onDelete = null,
            Action? onEdit = null,
            Action? onSessionChanged = null,
            Action? onAddToAddressBook = null,
            Func<string?>? currentGroupResolver = null,
            Action<string>? onAssignGroup = null,
            Action<string>? onLaunchWeb = null,
            Action<string>? onLaunchSsh = null,
            Action<string>? onLaunchRdp = null,
            Action<string>? onAddToSessions = null)
        {
            var menu = new ContextMenuStrip();

            // v2.1.3 — Figma styling applied once at the end. We just declare
            // it here as a lambda-style reminder; actual call is just before
            // `return menu;` at the bottom of this method.

            // === Shadow is always the FIRST item ===
            // The user opens the context menu most often to shadow a session.
            // Putting it at the top matches muscle memory and saves a scan.
            if (sessionResolver != null)
            {
                AddShadowItem(menu, owner, computerResolver, sessionResolver);
                // End session is added at the BOTTOM of the menu (see end of Build).
            }
            else
            {
                AddShadowItemNoSession(menu, owner, computerResolver);
            }

            // === RDP connection (standard, not shadow) ===
            // v2.2.17: if onLaunchRdp callback is provided, use it (creates
            // an embedded RDP session in RdpManagerForm instead of external
            // mstsc). Fallback: external mstsc.
            var miRdp = menu.Items.Add("ПОДКЛЮЧИТЬСЯ (RDP)", MenuIcons.Get("play_green"));
            miRdp.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                if (string.IsNullOrEmpty(computer)) return;
                if (onLaunchRdp != null)
                {
                    onLaunchRdp(computer);
                }
                else if (!RemoteActions.LaunchRdp(computer))
                {
                    MessageBox.Show(owner, $"Не удалось запустить RDP для {computer}.",
                        "RDP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            // v2.1.98 — items 3 and 4: WEB and SSH shortcuts. Open the
            // corresponding in-app embedded viewer in RDP Manager for
            // this computer. Only added if the caller wired the callbacks.
            if (onLaunchWeb != null)
            {
                var miWeb = menu.Items.Add("ПОДКЛЮЧИТЬСЯ (WEB)", MenuIcons.Get("globe"));
                miWeb.Click += (s, e) =>
                {
                    var computer = computerResolver() ?? "";
                    if (!string.IsNullOrEmpty(computer)) onLaunchWeb(computer);
                };
            }
            if (onLaunchSsh != null)
            {
                var miSsh = menu.Items.Add("ПОДКЛЮЧИТЬСЯ (SSH)", MenuIcons.Get("tasks"));
                miSsh.Click += (s, e) =>
                {
                    var computer = computerResolver() ?? "";
                    if (!string.IsNullOrEmpty(computer)) onLaunchSsh(computer);
                };
            }

            // === Sessions-grid-only: Add to address book ===
            if (sessionResolver != null && onAddToAddressBook != null)
            {
                menu.Items.Add(new ToolStripSeparator());
                var miAdd = menu.Items.Add("Добавить в адресную книгу", MenuIcons.Get("add"));
                miAdd.Click += (s, e) => onAddToAddressBook();
            }

            // v2.2.19: "Добавить в сессии" — explicitly creates a session tab
            // in RdpManagerForm sidebar for this computer (RDP type, БЕЗ ГРУППЫ).
            // Separate from ПОДКЛЮЧИТЬСЯ which just connects without persisting.
            if (onAddToSessions != null)
            {
                var miAddSess = menu.Items.Add("Добавить в сессии", MenuIcons.Get("add"));
                miAddSess.Click += (s, e) =>
                {
                    var computer = computerResolver() ?? "";
                    if (!string.IsNullOrEmpty(computer)) onAddToSessions(computer);
                };
            }

            // v2.1.48 — "Отправить сообщение..." moved into ДОМЕН submenu
            // (was top-level between RDP and Copy).

            // Copy submenu
            menu.Items.Add(new ToolStripSeparator());
            AddCopySubmenu(menu, owner, computerResolver);

            // Info + management
            menu.Items.Add(new ToolStripSeparator());
            AddInfoItem(menu, owner, computerResolver);
            AddLauncher(menu, "Computer Management (MMC)", "gear",
                owner, computerResolver, c => RemoteActions.OpenMsc(c, "compmgmt.msc"));
            AddLauncher(menu, "Службы (services.msc)", "list",
                owner, computerResolver, c => RemoteActions.OpenMsc(c, "services.msc"));
            AddLauncher(menu, "Просмотр событий (eventvwr)", "list",
                owner, computerResolver, c => RemoteActions.OpenMsc(c, "eventvwr.msc"));
            AddLauncher(menu, "События (WMI)...", "list",
                owner, computerResolver, c =>
                {
                    using var dlg = new EventLogForm(c);
                    UiModal.ShowDimmed(owner, dlg);
                    return true;
                });
            AddLauncher(menu, "Диспетчер задач...", "tasks",
                owner, computerResolver, c =>
                {
                    using var dlg = new TaskManagerForm(c);
                    UiModal.ShowDimmed(owner, dlg);
                    return true;
                });

            // ДОМЕН submenu
            menu.Items.Add(new ToolStripSeparator());
            AddDomainSubmenu(menu, owner, computerResolver, userResolver);

            // Power
            menu.Items.Add(new ToolStripSeparator());
            AddRestartItem(menu, owner, computerResolver);

            // Ping + diag
            menu.Items.Add(new ToolStripSeparator());
            AddPingCmdItem(menu, owner, computerResolver);
            AddLauncher(menu, "Открыть диск C$", "computer",
                owner, computerResolver, c => RemoteActions.OpenCShare(c));

            // === End session at the BOTTOM ===
            // It's a destructive action and rarely used — pushed to the end
            // so it's separated from common operations by a separator.
            if (sessionResolver != null)
            {
                menu.Items.Add(new ToolStripSeparator());
                AddEndItem(menu, owner, computerResolver, sessionResolver, onSessionChanged);
            }

            // Edit + Delete + Assign Group (address book only)
            if (onEdit != null || onDelete != null || onAssignGroup != null)
            {
                menu.Items.Add(new ToolStripSeparator());
                if (onAssignGroup != null && currentGroupResolver != null)
                {
                    AddAssignGroupSubmenu(menu, currentGroupResolver, onAssignGroup);
                }
                if (onEdit != null)
                {
                    var miEdit = menu.Items.Add("Редактировать...", MenuIcons.Get("info"));
                    miEdit.Click += (s, e) => onEdit();
                }
                if (onDelete != null)
                {
                    var miDelete = menu.Items.Add("Удалить", MenuIcons.Get("delete"));
                    miDelete.Click += (s, e) => onDelete();
                }
            }

            // v2.1.3 — apply Figma colour palette + uniform padding to every
            // item (including submenu items). Skipped if menu.Items is empty.
            FigmaMenuStyle.Apply(menu);

            return menu;
        }

        // "Назначить группу" submenu — shows 4 group items + "Без группы".
        // The current group is marked with a checkmark. Built dynamically on
        // each Opening so the checkmark tracks the current selection.
        private static void AddAssignGroupSubmenu(ContextMenuStrip menu,
            Func<string?> currentGroupResolver, Action<string> onAssignGroup)
        {
            var miParent = (ToolStripMenuItem)menu.Items.Add("Назначить группу", MenuIcons.Get("tag"));

            // Re-evaluate the checkmark every time the parent menu opens.
            // Without this the check reflects whatever row was selected
            // when the menu was first built.
            menu.Opening += (s, e) =>
            {
                var current = currentGroupResolver() ?? "";
                foreach (ToolStripMenuItem item in miParent.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    item.Checked = string.Equals(item.Tag as string, current,
                        StringComparison.OrdinalIgnoreCase);
                }
            };

            foreach (var info in ComputerGroup.All)
            {
                var mi = (ToolStripMenuItem)miParent.DropDownItems.Add(info.Label);
                mi.Tag = info.Key;
                mi.Click += (s, e) => onAssignGroup(info.Key);
            }

            miParent.DropDownItems.Add(new ToolStripSeparator());

            var miNone = (ToolStripMenuItem)miParent.DropDownItems.Add("Без группы");
            miNone.Tag = ComputerGroup.None;
            miNone.Click += (s, e) => onAssignGroup(ComputerGroup.None);
        }

        // === Individual item builders ===

        private static void AddShadowItem(ContextMenuStrip menu, IWin32Window owner,
            Func<string?> computerResolver, Func<int> sessionResolver)
        {
            var mi = menu.Items.Add("ПОДКЛЮЧИТЬСЯ (SHADOW)", MenuIcons.Get("play"));
            mi.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                if (string.IsNullOrEmpty(computer)) return;
                var sid = sessionResolver();
                if (sid <= 0)
                {
                    MessageBox.Show(owner, "Сессия не выбрана.", "Shadow",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { ShadowLauncher.Run(sid, computer, withControl: true, noConsent: false); }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, $"Не удалось запустить mstsc: {ex.Message}", "Shadow",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
        }

        // Address-book variant — auto-queries qwinsta for sessions.
        // v3.5.3 — reverted the "В окне / На весь экран" dropdown: mstsc.exe
        // does NOT honour the /f flag for /shadow connections (only for
        // regular RDP). The dropdown gave a false choice.
        private static void AddShadowItemNoSession(ContextMenuStrip menu, IWin32Window owner,
            Func<string?> computerResolver)
        {
            var mi = menu.Items.Add("ПОДКЛЮЧИТЬСЯ (SHADOW)", MenuIcons.Get("play"));
            mi.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                if (string.IsNullOrEmpty(computer)) return;
                var result = QwinstaService.Query(computer);
                if (result.Sessions.Count == 0)
                {
                    MessageBox.Show(owner,
                        $"На {computer} не найдено активных сессий.{(string.IsNullOrEmpty(result.ErrorText) ? "" : "\n" + result.ErrorText)}",
                        "Shadow", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var sess = result.Sessions[0];
                try { ShadowLauncher.Run(sess.Id, computer, withControl: true, noConsent: false); }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, $"Не удалось запустить mstsc: {ex.Message}", "Shadow",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
        }

        private static void AddEndItem(ContextMenuStrip menu, IWin32Window owner,
            Func<string?> computerResolver, Func<int> sessionResolver, Action? onSessionChanged)
        {
            var mi = menu.Items.Add("Завершить сессию", MenuIcons.Get("stop"));
            mi.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                if (string.IsNullOrEmpty(computer)) return;
                var sid = sessionResolver();
                if (sid <= 0)
                {
                    MessageBox.Show(owner, "Сессия не выбрана.", "Завершить",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var r = MessageBox.Show(owner,
                    $"Завершить сессию {sid} на {computer}?\n\nПользователь будет разлогинен.",
                    "Завершить сессию", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
                if (RemoteActions.EndSession(computer, sid))
                {
                    MessageBox.Show(owner, "Сессия завершена.", "Завершить",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    onSessionChanged?.Invoke();
                }
                else
                {
                    MessageBox.Show(owner, "Не удалось завершить сессию.", "Завершить",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
        }

        private static void AddMessageItem(ContextMenuStrip menu, IWin32Window owner,
            Func<string?> computerResolver)
        {
            var mi = menu.Items.Add("Отправить сообщение...", MenuIcons.Get("msg"));
            mi.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                if (string.IsNullOrEmpty(computer)) return;
                var text = PromptDialog.Show(owner, "Сообщение пользователю",
                    $"Введите сообщение для {computer}:", "");
                if (string.IsNullOrEmpty(text)) return;
                if (RemoteActions.SendMessage(computer, text))
                    MessageBox.Show(owner, "Сообщение отправлено.", "Сообщение",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(owner, "Не удалось отправить сообщение.", "Сообщение",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
        }

        private static void AddCopySubmenu(ContextMenuStrip menu, IWin32Window owner,
            Func<string?> computerResolver)
        {
            var miCopy = (ToolStripMenuItem)menu.Items.Add("Скопировать", MenuIcons.Get("copy"));

            var miCopyName = miCopy.DropDownItems.Add("Имя компьютера", MenuIcons.Get("computer"));
            miCopyName.Click += (s, e) =>
            {
                var computer = computerResolver();
                if (string.IsNullOrEmpty(computer)) return;
                try { Clipboard.SetText(computer); } catch { }
            };

            var miCopyIP = miCopy.DropDownItems.Add("IP-адрес(а)", MenuIcons.Get("globe"));
            miCopyIP.Click += (s, e) =>
            {
                var computer = computerResolver();
                if (string.IsNullOrEmpty(computer)) return;
                var ip = LookupIpFromAddressBook(computer);
                if (!string.IsNullOrEmpty(ip))
                {
                    try { Clipboard.SetText(ip); } catch { }
                    return;
                }
                // v3.9.3 Фаза 2 — WMI в фоне: меню не висит на недоступном
                // хосте; результат — в буфер + сообщение.
                CopyIpViaWmiAsync(owner, computer);
            };
        }

        private static void AddInfoItem(ContextMenuStrip menu, IWin32Window owner,
            Func<string?> computerResolver)
        {
            var mi = menu.Items.Add("Информация о компьютере...", MenuIcons.Get("info"));
            mi.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                if (string.IsNullOrEmpty(computer)) return;
                using var dlg = new ComputerInfoForm(computer);
                UiModal.ShowDimmed(owner, dlg);
            };
        }

        // ДОМЕН submenu — uses AD for FullName lookup, stubs for the rest.
        private static void AddDomainSubmenu(ContextMenuStrip menu, IWin32Window owner,
            Func<string?> computerResolver, Func<string?>? userResolver)
        {
            var miDomain = (ToolStripMenuItem)menu.Items.Add("ДОМЕН", MenuIcons.Get("shield"));

            var miFullName = miDomain.DropDownItems.Add("Показать ФИО учётки...", MenuIcons.Get("person"));
            miFullName.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                if (string.IsNullOrEmpty(computer)) return;
                var user = (userResolver?.Invoke()) ?? "";
                if (string.IsNullOrEmpty(user))
                {
                    MessageBox.Show(owner,
                        "Сначала выберите сессию (пользователя) в списке.",
                        "ФИО учётки", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var sam = user.Contains('\\') ? user.Substring(user.IndexOf('\\') + 1) : user;
                var fullName = AdService.GetDisplayName(sam);
                if (string.IsNullOrEmpty(fullName))
                    MessageBox.Show(owner, $"ФИО не найдено для '{user}'.", "ФИО учётки",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(owner, $"{user} → {fullName}", "ФИО учётки",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            miDomain.DropDownItems.Add(new ToolStripSeparator());

            AddDomainLauncher(miDomain, "Управление службами (grid)...", "gear",
                owner, computerResolver, c => RemoteActions.OpenMsc(c, "services.msc"));
            AddDomainLauncher(miDomain, "Локальные администраторы...", "shield",
                owner, computerResolver, c =>
                {
                    using var dlg = new LocalAdminsForm(c);
                    UiModal.ShowDimmed(owner, dlg);
                    return true;
                });
            AddDomainLauncher(miDomain, "Group Policy Results...", "list",
                owner, computerResolver, c =>
                {
                    using var dlg = new GpResultsForm(c);
                    UiModal.ShowDimmed(owner, dlg);
                    return true;
                });
            AddDomainLauncher(miDomain, "Поиск где залогинен пользователь...", "search",
                owner, computerResolver, c =>
                {
                    // Pre-populate with the user from the right-clicked row +
                    // auto-start the search — typical flow is "I see this
                    // user, where else are they logged in?".
                    var user = userResolver?.Invoke();
                    using var dlg = new FindUserDialog(c, user, autoStart: true);
                    UiModal.ShowDimmed(owner, dlg);
                    return true;
                });
            AddDomainLauncher(miDomain, "Открытые файлы (все серверы)...", "list",
                owner, computerResolver, c =>
                {
                    using var dlg = new OpenFilesForm();
                    UiModal.ShowDimmed(owner, dlg);
                    return true;
                });

            // v2.1.48 — "Отправить сообщение..." was a top-level item, moved
            // here per user request. Sits at the bottom of ДОМЕН because it's
            // rarely used and the search/find items above are more common.
            miDomain.DropDownItems.Add(new ToolStripSeparator());
            var miMsg = miDomain.DropDownItems.Add("Отправить сообщение...", MenuIcons.Get("msg"));
            miMsg.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                if (string.IsNullOrEmpty(computer)) return;
                var text = PromptDialog.Show(owner, "Сообщение пользователю",
                    $"Введите сообщение для {computer}:", "");
                if (string.IsNullOrEmpty(text)) return;
                if (RemoteActions.SendMessage(computer, text))
                    MessageBox.Show(owner, "Сообщение отправлено.", "Сообщение",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(owner, "Не удалось отправить сообщение.", "Сообщение",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
        }

        private static void AddDomainStub(ToolStripMenuItem parent, string label, string icon)
        {
            var mi = parent.DropDownItems.Add(label, MenuIcons.Get(icon));
            mi.Click += (s, e) =>
            {
                MessageBox.Show(parent.Owner, $"'{label}' будет добавлено позже.",
                    "TODO", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
        }

        // Helper for domain-submenu launchers that take a computer.
        private static void AddDomainLauncher(ToolStripMenuItem parent, string label, string icon,
            IWin32Window owner, Func<string?> computerResolver, Func<string, bool> action)
        {
            var mi = parent.DropDownItems.Add(label, MenuIcons.Get(icon));
            mi.Click += (s, e) =>
            {
                var computer = computerResolver();
                if (string.IsNullOrEmpty(computer)) return;
                action(computer);
            };
        }

        private static void AddRestartItem(ContextMenuStrip menu, IWin32Window owner,
            Func<string?> computerResolver)
        {
            var mi = menu.Items.Add("Перезагрузить компьютер", MenuIcons.Get("restart"));
            mi.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                if (string.IsNullOrEmpty(computer)) return;
                var r = MessageBox.Show(owner,
                    $"Перезагрузить {computer}?\n\nПользователь будет разлогинен.",
                    "Перезагрузка", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
                if (RemoteActions.Restart(computer))
                    MessageBox.Show(owner, "Перезагрузка инициирована.", "Перезагрузка",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(owner, "Не удалось.", "Перезагрузка",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
        }

        private static void AddPingCmdItem(ContextMenuStrip menu, IWin32Window owner,
            Func<string?> computerResolver)
        {
            var mi = menu.Items.Add("Пинг...", MenuIcons.Get("ping"));
            mi.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                if (string.IsNullOrEmpty(computer)) return;
                try { PingService.OpenInCmd(computer); }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, $"Не удалось открыть cmd: {ex.Message}", "Пинг",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            // v2.2.2: Tracert right under Ping — same external cmd window.
            // Uses tracert.exe which is built into Windows. Traces the
            // network path from this machine to the target, hop by hop.
            var miTracert = menu.Items.Add("Tracert...", MenuIcons.Get("ping"));
            miTracert.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                if (string.IsNullOrEmpty(computer)) return;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                    {
                        Arguments = $"/k tracert {computer}",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, $"Не удалось открыть cmd: {ex.Message}", "Tracert",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            // v3.9.4 — TCP-порт-сканер (тот же вход, что «Инструменты →
            // Проверка портов», но хост подставлен из контекста).
            var miPorts = menu.Items.Add("Проверка портов...", MenuIcons.Get("ping"));
            miPorts.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                try
                {
                    Forms.PortScanForm.Show(owner, string.IsNullOrEmpty(computer) ? null : computer);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, $"Не удалось открыть сканер: {ex.Message}", "Проверка портов",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            // v3.9.4 — удалённый терминал (WinRM/WMI) для этого хоста.
            var miConsole = menu.Items.Add("Терминал...", MenuIcons.Get("terminal"));
            miConsole.Click += (s, e) =>
            {
                var computer = computerResolver() ?? "";
                try
                {
                    Forms.RemoteConsoleForm.Show(owner, string.IsNullOrEmpty(computer) ? null : computer);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, $"Не удалось открыть терминал: {ex.Message}", "Терминал",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
        }

        // Generic launcher for items that just call a Func<bool>.
        private static void AddLauncher(ContextMenuStrip menu, string label, string icon,
            IWin32Window owner, Func<string?> computerResolver, Func<string, bool> action)
        {
            var mi = menu.Items.Add(label, MenuIcons.Get(icon));
            mi.Click += (s, e) =>
            {
                var computer = computerResolver();
                if (string.IsNullOrEmpty(computer)) return;
                if (!action(computer))
                {
                    // Action returned false but didn't throw — check if it was
                    // a TODO stub (TODOs already show their own MessageBox).
                    // We can't distinguish here cheaply, so only show error if
                    // the action explicitly returned false AND no message was
                    // already shown. For simplicity, swallow false here.
                }
            };
        }

        // Lookup IP from address book — avoids a WMI round-trip when the
        // entry already has IPs cached.
        private static string LookupIpFromAddressBook(string computer)
        {
            try
            {
                var entries = AddressBookStore.Load();
                var match = entries.FirstOrDefault(e =>
                    string.Equals(e.Computer, computer, StringComparison.OrdinalIgnoreCase));
                return match?.IPs ?? "";
            }
            catch { return ""; }
        }

        // v3.9.3 Фаза 2 — WMI-опрос в фоне: меню не висит на недоступном
        // хосте. Clipboard требует STA → результат маршалим в UI-поток
        // владельца; при неудаче — сообщение, при успехе IP уже в буфере.
        private static void CopyIpViaWmiAsync(IWin32Window owner, string computer)
        {
            TaskScheduler ui;
            try { ui = TaskScheduler.FromCurrentSynchronizationContext(); }
            catch { return; }
            Task.Run(async () =>
            {
                try { return await WmiService.GetNetworkConfigAsync(computer); }
                catch { return null; }
            }).ContinueWith(t =>
            {
                try
                {
                    var net = t.Result;
                    string? ip = (net != null && net.IPv4Addresses.Count > 0)
                        ? string.Join(", ", net.IPv4Addresses) : null;
                    if (string.IsNullOrEmpty(ip))
                        MessageBox.Show(owner, "IP не найден.", "Скопировать",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else
                    {
                        try { Clipboard.SetText(ip); } catch { }
                    }
                }
                catch { }
            }, ui);
        }

        // v3.9.0 — themed variant of Build() that returns a custom
        // ThemedContextMenu popup instead of a ContextMenuStrip.
        public static ThemedContextMenu BuildThemed(
            IWin32Window owner,
            Func<string?> computerResolver,
            Func<int>? sessionResolver = null,
            Func<string?>? userResolver = null,
            Action? onDelete = null,
            Action? onEdit = null,
            Action? onSessionChanged = null,
            Action? onAddToAddressBook = null,
            Func<string?>? currentGroupResolver = null,
            Action<string>? onAssignGroup = null,
            Action<string>? onLaunchWeb = null,
            Action<string>? onLaunchSsh = null,
            Action<string>? onLaunchRdp = null,
            Action<string>? onAddToSessions = null,
            Action<string>? onSendFile = null)
        {
            var menu = new ThemedContextMenu();
            menu.AddHeader(computerResolver() ?? "", null);

            if (sessionResolver != null)
            {
                menu.AddItem("ПОДКЛЮЧИТЬСЯ (SHADOW)", "play", () =>
                {
                    var computer = computerResolver() ?? "";
                    if (string.IsNullOrEmpty(computer)) return;
                    var sid = sessionResolver();
                    if (sid <= 0)
                    {
                        MessageBox.Show(owner, "Сессия не выбрана.", "Shadow", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    try { ShadowLauncher.Run(sid, computer, withControl: true, noConsent: false); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(owner, $"Не удалось запустить mstsc: {ex.Message}", "Shadow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                });
            }
            else
            {
                menu.AddItem("ПОДКЛЮЧИТЬСЯ (SHADOW)", "play", () =>
                {
                    var computer = computerResolver() ?? "";
                    if (string.IsNullOrEmpty(computer)) return;
                    var result = QwinstaService.Query(computer);
                    if (result.Sessions.Count == 0)
                    {
                        MessageBox.Show(owner, $"На {computer} не найдено активных сессий.{(string.IsNullOrEmpty(result.ErrorText) ? "" : "\n" + result.ErrorText)}", "Shadow", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    var sess = result.Sessions[0];
                    try { ShadowLauncher.Run(sess.Id, computer, withControl: true, noConsent: false); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(owner, $"Не удалось запустить mstsc: {ex.Message}", "Shadow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                });
            }

            menu.AddItem("ПОДКЛЮЧИТЬСЯ (RDP)", "play_green", () =>
            {
                var computer = computerResolver() ?? "";
                if (string.IsNullOrEmpty(computer)) return;
                if (onLaunchRdp != null) onLaunchRdp(computer);
                else if (!RemoteActions.LaunchRdp(computer))
                    MessageBox.Show(owner, $"Не удалось запустить RDP для {computer}.", "RDP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });

            if (onLaunchWeb != null)
                menu.AddItem("ПОДКЛЮЧИТЬСЯ (WEB)", "globe", () => { var c = computerResolver(); if (!string.IsNullOrEmpty(c)) onLaunchWeb(c); });
            if (onLaunchSsh != null)
                menu.AddItem("ПОДКЛЮЧИТЬСЯ (SSH)", "tasks", () => { var c = computerResolver(); if (!string.IsNullOrEmpty(c)) onLaunchSsh(c); });

            if (sessionResolver != null && onAddToAddressBook != null)
            {
                menu.AddSeparator();
                menu.AddItem("Добавить в адресную книгу", "add", () => onAddToAddressBook());
            }
            if (onAddToSessions != null)
            {
                menu.AddItem("Добавить в сессии", "add", () => { var c = computerResolver(); if (!string.IsNullOrEmpty(c)) onAddToSessions(c); });
            }

            menu.AddSeparator();
            AddCopySubmenuThemed(menu, owner, computerResolver);

            menu.AddSeparator();
            menu.AddItem("Информация о компьютере...", "info", () =>
            {
                var c = computerResolver() ?? "";
                if (string.IsNullOrEmpty(c)) return;
                using var dlg = new ComputerInfoForm(c);
                UiModal.ShowDimmed(owner, dlg);
            });
            menu.AddItem("Computer Management (MMC)", "gear", () => { var c = computerResolver(); if (!string.IsNullOrEmpty(c)) RemoteActions.OpenMsc(c, "compmgmt.msc"); });
            menu.AddItem("Службы (services.msc)", "list", () => { var c = computerResolver(); if (!string.IsNullOrEmpty(c)) RemoteActions.OpenMsc(c, "services.msc"); });
            menu.AddItem("Просмотр событий (eventvwr)", "list", () => { var c = computerResolver(); if (!string.IsNullOrEmpty(c)) RemoteActions.OpenMsc(c, "eventvwr.msc"); });
            menu.AddItem("События (WMI)...", "list", () => { var c = computerResolver(); if (!string.IsNullOrEmpty(c)) { using var dlg = new EventLogForm(c);                 UiModal.ShowDimmed(owner, dlg); } });
            // v4.0.0.1 — ИИ-помощник: дефолтная выборка (Error, последние 48 часов).
            menu.AddItem("ИИ-анализ событий", "info", async () =>
            {
                var c = computerResolver();
                if (string.IsNullOrEmpty(c)) return;
                if (!Helpers.AiEventAnalyzer.Gate(owner)) return;
                try
                {
                    var aggs = await Helpers.AiEventAnalyzer.CollectDefaultAsync(c);
                    await Helpers.AiEventAnalyzer.RunAsync(owner, c, "Система+Приложение • Error • 48 часов", aggs);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, $"Не удалось собрать события с {c}: {ex.Message}", "ИИ-анализ",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
            menu.AddItem("Диспетчер задач...", "tasks", () => { var c = computerResolver(); if (!string.IsNullOrEmpty(c)) { using var dlg = new TaskManagerForm(c);                 UiModal.ShowDimmed(owner, dlg); } });
            // v4.0.0.1 — удалённый терминал (WinRM/WMI) для этого хоста, как в старом меню.
            menu.AddItem("Удалённый терминал...", "terminal", () =>
            {
                var c = computerResolver();
                if (string.IsNullOrEmpty(c)) return;
                try
                {
                    Forms.RemoteConsoleForm.Show(owner, c);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(owner, $"Не удалось открыть терминал: {ex.Message}", "Терминал",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
            // v4.0 — dashboard monitoring toggle
            {
                var c = computerResolver() ?? "";
                if (!string.IsNullOrEmpty(c))
                {
                    var entries = Services.AddressBookStore.Load();
                    var ent = entries.FirstOrDefault(e => string.Equals(e.Computer, c, StringComparison.OrdinalIgnoreCase));
                    bool isMon = ent != null && ent.Monitored;
                    menu.AddItem(isMon ? "Отслеживать события ✓" : "Отслеживать события", "dashboard", () =>
                    {
                        var all = Services.AddressBookStore.Load();
                        var target = all.FirstOrDefault(e => string.Equals(e.Computer, c, StringComparison.OrdinalIgnoreCase));
                        if (target == null) { MessageBox.Show(owner, $"Компьютер {c} не найден в книге.", "Дашбоард", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                        target.Monitored = !target.Monitored;
                        Services.AddressBookStore.Save(all);
                        Services.EventDashboardStore.SaveMonitoredComputers(all.Where(e => e.Monitored).Select(e => e.Computer).ToList());
                        MessageBox.Show(owner, target.Monitored ? $"Добавлен в дашбоард: {c}" : $"Убран из дашборда: {c}", "Дашбоард", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
            }
            // v3.9.1 — SNMP monitoring toggle (Zabbix dash without agent)
            {
                var c = computerResolver() ?? "";
                if (!string.IsNullOrEmpty(c))
                {
                    var entries = Services.AddressBookStore.Load();
                    var ent = entries.FirstOrDefault(e => string.Equals(e.Computer, c, StringComparison.OrdinalIgnoreCase));
                    bool isSnmp = ent != null && ent.SnmpMonitored;
                    menu.AddItem(isSnmp ? "Отслеживать по SNMP ✓" : "Отслеживать по SNMP", "globe", () =>
                    {
                        var all = Services.AddressBookStore.Load();
                        var target = all.FirstOrDefault(e => string.Equals(e.Computer, c, StringComparison.OrdinalIgnoreCase));
                        if (target == null) { MessageBox.Show(owner, $"Компьютер {c} не найден в книге.", "Дашбоард", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                        target.SnmpMonitored = !target.SnmpMonitored;
                        Services.AddressBookStore.Save(all);
                        MessageBox.Show(owner, target.SnmpMonitored ? $"SNMP-мониторинг включён: {c} (опрос по UDP 161)" : $"SNMP-мониторинг отключён: {c}", "Дашбоард", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
            }

            menu.AddSeparator();
            AddDomainSubmenuThemed(menu, owner, computerResolver, userResolver);

            menu.AddSeparator();
            menu.AddItem("Перезагрузить компьютер", "restart", () =>
            {
                var c = computerResolver() ?? "";
                if (string.IsNullOrEmpty(c)) return;
                var r = MessageBox.Show(owner, $"Перезагрузить {c}?\n\nПользователь будет разлогинен.", "Перезагрузка", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
                if (RemoteActions.Restart(c)) MessageBox.Show(owner, "Перезагрузка инициирована.", "Перезагрузка", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else MessageBox.Show(owner, "Не удалось.", "Перезагрузка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });

            menu.AddSeparator();
            menu.AddItem("Пинг...", "ping", () => { var c = computerResolver(); if (!string.IsNullOrEmpty(c)) { try { PingService.OpenInCmd(c); } catch (Exception ex) { MessageBox.Show(owner, $"Не удалось открыть cmd: {ex.Message}", "Пинг", MessageBoxButtons.OK, MessageBoxIcon.Warning); } } });
            menu.AddItem("Tracert...", "ping", () => { var c = computerResolver(); if (!string.IsNullOrEmpty(c)) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { Arguments = $"/k tracert {c}", UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(owner, $"Не удалось открыть cmd: {ex.Message}", "Tracert", MessageBoxButtons.OK, MessageBoxIcon.Warning); } } });
            menu.AddItem("Открыть диск C$", "computer", () => { var c = computerResolver(); if (!string.IsNullOrEmpty(c)) RemoteActions.OpenCShare(c); });
            if (onSendFile != null)
                menu.AddItem("Передать файл…", "computer", () => { var c = computerResolver(); if (!string.IsNullOrEmpty(c)) onSendFile(c); });

            if (sessionResolver != null)
            {
                menu.AddSeparator();
                menu.AddItem("Завершить сессию", "stop", () =>
                {
                    var c = computerResolver() ?? "";
                    if (string.IsNullOrEmpty(c)) return;
                    var sid = sessionResolver();
                    if (sid <= 0) { MessageBox.Show(owner, "Сессия не выбрана.", "Завершить", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                    var r = MessageBox.Show(owner, $"Завершить сессию {sid} на {c}?\n\nПользователь будет разлогинен.", "Завершить сессию", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) return;
                    if (RemoteActions.EndSession(c, sid)) { MessageBox.Show(owner, "Сессия завершена.", "Завершить", MessageBoxButtons.OK, MessageBoxIcon.Information); onSessionChanged?.Invoke(); }
                    else MessageBox.Show(owner, "Не удалось завершить сессию.", "Завершить", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            }

            if (onEdit != null || onDelete != null || onAssignGroup != null)
            {
                menu.AddSeparator();
                if (onAssignGroup != null && currentGroupResolver != null)
                    AddAssignGroupSubmenuThemed(menu, owner, currentGroupResolver, onAssignGroup);
                if (onEdit != null)
                    menu.AddItem("Редактировать...", "info", () => onEdit());
                if (onDelete != null)
                    menu.AddItem("Удалить", "delete", () => onDelete());
            }

            return menu;
        }

        private static void AddAssignGroupSubmenuThemed(ThemedContextMenu menu, IWin32Window owner,
            Func<string?> currentGroupResolver, Action<string> onAssignGroup)
        {
            menu.AddSubmenu("Назначить группу", "tag", sub =>
            {
                var current = currentGroupResolver() ?? "";
                var counts = GroupCounts();
                foreach (var info in ComputerGroup.All)
                {
                    int cnt = counts.TryGetValue(info.Key, out var n) ? n : 0;
                    var key = info.Key;
                    sub.AddItem(info.Label, "folder", () => onAssignGroup(key), cnt.ToString(),
                        string.Equals(key, current, StringComparison.OrdinalIgnoreCase));
                }
                sub.AddSeparator();
                sub.AddItem("Без группы", "folder", () => onAssignGroup(ComputerGroup.None), "",
                    string.Equals(ComputerGroup.None, current, StringComparison.OrdinalIgnoreCase));
                sub.AddSeparator();
                sub.AddItem("Новая группа...", "add", () =>
                {
                    var res = GroupEditDialog.Show(owner, null);
                    if (res == null) return;
                    var key = ComputerGroup.GenerateKey(res.Name);
                    ComputerGroup.AddGroup(key, res.Name, res.Accent);
                    onAssignGroup(key);
                });
            });
        }

        private static void AddCopySubmenuThemed(ThemedContextMenu menu, IWin32Window owner, Func<string?> computerResolver)
        {
            menu.AddSubmenu("Скопировать", "copy", sub =>
            {
                sub.AddItem("Имя компьютера", "computer", () =>
                {
                    var c = computerResolver();
                    if (string.IsNullOrEmpty(c)) return;
                    try { Clipboard.SetText(c); } catch { }
                });
                sub.AddItem("IP-адрес(а)", "globe", () =>
                {
                    var c = computerResolver();
                    if (string.IsNullOrEmpty(c)) return;
                    var ip = LookupIpFromAddressBook(c);
                    if (!string.IsNullOrEmpty(ip))
                    {
                        try { Clipboard.SetText(ip); } catch { }
                        return;
                    }
                    // v3.9.3 Фаза 2 — WMI в фоне (см. выше).
                    CopyIpViaWmiAsync(owner, c);
                });
            });
        }

        private static void AddDomainSubmenuThemed(ThemedContextMenu menu, IWin32Window owner,
            Func<string?> computerResolver, Func<string?>? userResolver)
        {
            menu.AddSubmenu("ДОМЕН", "shield", sub =>
            {
                sub.AddItem("Показать ФИО учётки...", "person", () =>
                {
                    var c = computerResolver() ?? "";
                    if (string.IsNullOrEmpty(c)) return;
                    var user = (userResolver?.Invoke()) ?? "";
                    if (string.IsNullOrEmpty(user)) { MessageBox.Show(owner, "Сначала выберите сессию (пользователя) в списке.", "ФИО учётки", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                    var sam = user.Contains('\\') ? user.Substring(user.IndexOf('\\') + 1) : user;
                    var fullName = AdService.GetDisplayName(sam);
                    if (string.IsNullOrEmpty(fullName)) MessageBox.Show(owner, $"ФИО не найдено для '{user}'.", "ФИО учётки", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else MessageBox.Show(owner, $"{user} → {fullName}", "ФИО учётки", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
                sub.AddSeparator();
                AddDomainLauncherThemed(sub, "Управление службами (grid)...", "gear", owner, computerResolver, c => RemoteActions.OpenMsc(c, "services.msc"));
                AddDomainLauncherThemed(sub, "Локальные администраторы...", "shield", owner, computerResolver, c => { using var dlg = new LocalAdminsForm(c);                 UiModal.ShowDimmed(owner, dlg); return true; });
                AddDomainLauncherThemed(sub, "Group Policy Results...", "list", owner, computerResolver, c => { if (!ModuleGate.Require(owner, "domain")) return true; using var dlg = new GpResultsForm(c);                 UiModal.ShowDimmed(owner, dlg); return true; });
                AddDomainLauncherThemed(sub, "Поиск где залогинен пользователь...", "search", owner, computerResolver, c => { if (!ModuleGate.Require(owner, "domain")) return true; var user = userResolver?.Invoke(); using var dlg = new FindUserDialog(c, user, autoStart: true);                 UiModal.ShowDimmed(owner, dlg); return true; });
                AddDomainLauncherThemed(sub, "Открытые файлы (все серверы)...", "list", owner, computerResolver, c => { if (!ModuleGate.Require(owner, "domain")) return true; using var dlg = new OpenFilesForm();                 UiModal.ShowDimmed(owner, dlg); return true; });
                AddDomainLauncherThemed(sub, "Удаленный реестр...", "key", owner, computerResolver, c => { using var dlg = new Forms.RemoteRegistryForm(c);                 UiModal.ShowDimmed(owner, dlg); return true; });
                sub.AddSeparator();
                sub.AddItem("Отправить сообщение...", "msg", () =>
                {
                    var c = computerResolver() ?? "";
                    if (string.IsNullOrEmpty(c)) return;
                    var text = PromptDialog.Show(owner, "Сообщение пользователю", $"Введите сообщение для {c}:", "");
                    if (string.IsNullOrEmpty(text)) return;
                    if (RemoteActions.SendMessage(c, text)) MessageBox.Show(owner, "Сообщение отправлено.", "Сообщение", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else MessageBox.Show(owner, "Не удалось отправить сообщение.", "Сообщение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            });
        }

        private static void AddDomainLauncherThemed(ThemedContextMenu menu, string label, string icon, IWin32Window owner, Func<string?> computerResolver, Func<string, bool> action)
        {
            menu.AddItem(label, icon, () =>
            {
                var c = computerResolver();
                if (string.IsNullOrEmpty(c)) return;
                action(c);
            });
        }

        private static Dictionary<string, int> GroupCounts()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var e in AddressBookStore.Load())
                {
                    var key = string.IsNullOrEmpty(e.Group) ? ComputerGroup.None : e.Group;
                    if (!d.ContainsKey(key)) d[key] = 0;
                    d[key]++;
                }
            }
            catch { }
            return d;
        }
    }
}
