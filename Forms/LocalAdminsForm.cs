using System;
using System.Collections.Generic;
using System.Drawing;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Forms;
using ShadowRDP.Controls;
using ShadowRDP.Services;

namespace ShadowRDP.Forms
{
    // "Локальные администраторы" dialog. Lists members of the local
    // Administrators group on the remote computer via WMI.
    //
    // WMI queries:
    //   1. Win32_Group.Name='Administrators', Domain='<computer>'  → SID + path
    //   2. Win32_GroupUser.GroupComponent=<group path>             → PartComponent
    //      (refs Win32_UserAccount or Win32_Group)
    //   3. Resolve each member to Name + Domain + SID + account type
    //
    // Slow on first run (~3-5 seconds) — WMI joins are not indexed.
    public class LocalAdminsForm : Form
    {
        private readonly string _computer;
        private readonly DataGridView _grid;
        private readonly Label _refreshBtn;
        private readonly Label _closeBtn;
        private readonly Label _statusLabel;

        public LocalAdminsForm(string computer)
        {
            _computer = computer;
            Text = $"Локальные администраторы на {computer}";
            ClientSize = new Size(640, 546);
            MinimumSize = new Size(480, 386);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = ShadowRDP.Helpers.UiTheme.Background;
            FormBorderStyle = FormBorderStyle.Sizable;

            // Header
            var headerPanel = Helpers.UiHeader.Create("Локальные администраторы", _computer, "shield");
            headerPanel.Dock = DockStyle.Top;
            Controls.Add(headerPanel);

            _grid = new DataGridView
            {
                Location = new Point(16, 82),
                Size = new Size(608, 400),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowUserToAddRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = ShadowRDP.Helpers.UiTheme.Card,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = ShadowRDP.Helpers.UiTheme.Background,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                DefaultCellStyle = {
                    SelectionBackColor = ShadowRDP.Helpers.UiTheme.AccentSoft,
                    SelectionForeColor = ShadowRDP.Helpers.UiTheme.Card
                },
                ColumnHeadersDefaultCellStyle = {
                    BackColor = ShadowRDP.Helpers.UiTheme.Muted,
                    ForeColor = ShadowRDP.Helpers.UiTheme.MutedForeground,
                    Font = new Font("Segoe UI Semibold", 9F)
                },
                RowTemplate = { Height = 24 }
            };
_grid.EnableHeadersVisualStyles = false;
            var cName = new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Имя", FillWeight = 130 };
            var cDomain = new DataGridViewTextBoxColumn { Name = "Domain", HeaderText = "Домен", FillWeight = 90 };
            var cType = new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Тип", FillWeight = 60 };
            var cSID = new DataGridViewTextBoxColumn { Name = "SID", HeaderText = "SID", FillWeight = 200 };
            _grid.Columns.AddRange(cName, cDomain, cType, cSID);

            // Right-click context menu — "Сменить пароль..." enabled only
            // when the selected row is a LOCAL user account (Domain matches
            // this computer and Type is "Пользователь"). For domain accounts
            // surfaced through local group membership, the menu item is
            // disabled with an explanation tooltip — we can't change a
            // domain account's password this way.
            // v3.9.0 — right-click menu shown as a themed popup.
            _grid.CellMouseDown += (sender, args) =>
            {
                if (args.Button != MouseButtons.Right) return;
                if (args.RowIndex < 0) return;
                _grid.ClearSelection();
                _grid.Rows[args.RowIndex].Selected = true;
                var menu = new ShadowRDP.Helpers.ThemedContextMenu();
                menu.AddHeader("Локальные админы");
                if (CanChangePasswordForSelection())
                    menu.AddItem("Сменить пароль...", "gear", () => OnChangePasswordClicked());
                menu.Show(this, Control.MousePosition);
            };

            ShadowRDP.Helpers.UiGridStyle.PaintHeaders(_grid);
            Controls.Add(_grid);

            _statusLabel = new Label
            {
                Location = new Point(16, 491),
                Size = new Size(380, 22),
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Text = "Загрузка..."
            };
            Controls.Add(_statusLabel);

            _refreshBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Обновить");
            _refreshBtn.Location = new Point(420, 488);
            _refreshBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _refreshBtn.Click += async (s, e) => await LoadAsync();
            Controls.Add(_refreshBtn);

            _closeBtn = ShadowRDP.Helpers.UiButtons.MakeFlat("Закрыть");
            _closeBtn.Location = new Point(534, 488);
            _closeBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _closeBtn.Click += (s, e) => Close();
            Controls.Add(_closeBtn);

            Shown += async (s, e) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            _grid.Rows.Clear();
            _statusLabel.Text = "Запрос...";
            _refreshBtn.Enabled = false;
            try
            {
                var members = await Task.Run(() => QueryAdmins(_computer));
                foreach (var m in members)
                {
                    _grid.Rows.Add(m.Name, m.Domain, m.Type, m.SID);
                }
                _statusLabel.Text = $"Найдено: {members.Count} участников";
                Logger.Info($"LocalAdmins on {_computer}: {members.Count} members");
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка: {ex.Message}";
            }
            finally
            {
                _refreshBtn.Enabled = true;
            }
        }

        // Context-menu guard. Returns true only if the currently selected
        // row is a LOCAL user account on this computer (not a domain account
        // surfaced through group membership, not a nested group).
        private bool CanChangePasswordForSelection()
        {
            if (_grid.SelectedRows.Count == 0) return false;
            var row = _grid.SelectedRows[0];
            var type = row.Cells["Type"].Value?.ToString() ?? "";
            var domain = row.Cells["Domain"].Value?.ToString() ?? "";
            var name = row.Cells["Name"].Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(name)) return false;
            if (!string.Equals(type, "Пользователь", StringComparison.OrdinalIgnoreCase))
                return false;
            // Domain must match the target computer (case-insensitive). WMI
            // returns the local computer name in Domain for local accounts,
            // and the AD domain name for domain accounts.
            return string.Equals(domain, _computer, StringComparison.OrdinalIgnoreCase);
        }

        private void OnChangePasswordClicked()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                MessageBox.Show(this, "Выберите запись.",
                    "Сменить пароль", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var row = _grid.SelectedRows[0];
            var name = row.Cells["Name"].Value?.ToString() ?? "";
            var domain = row.Cells["Domain"].Value?.ToString() ?? "";
            var type = row.Cells["Type"].Value?.ToString() ?? "";

            if (!string.Equals(type, "Пользователь", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "Сменить пароль можно только для локального пользователя, не для группы.",
                    "Сменить пароль", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!string.Equals(domain, _computer, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    $"Это доменная учётная запись ({domain}). Сменить пароль доменного\n" +
                    $"аккаунта локально нельзя — обратитесь к администратору домена.",
                    "Сменить пароль", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(this, "Не удалось определить имя пользователя.",
                    "Сменить пароль", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Step 1: prompt for password via the masked dialog.
            var password = PasswordDialog.Show(this,
                $"Сменить пароль — {_computer}\\{name}",
                $"Новый пароль для локального аккаунта '{name}' на {_computer}:");
            if (password == null) return;  // user cancelled

            // Step 2: explicit Yes/No confirmation. This is a destructive
            // operation — the new password takes effect immediately and
            // there's no rollback.
            var confirm = MessageBox.Show(this,
                $"Сменить пароль для {_computer}\\{name}?\n\n" +
                $"Действие необратимо. Убедитесь, что новый пароль сохранён\n" +
                $"в безопасном месте.",
                "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;

            // Step 3: invoke SetPassword via SAM RPC. Runs on the UI thread
            // is fine — first call can take 1-3 seconds (NetAPI init).
            _statusLabel.Text = $"Установка пароля для {name}...";
            Application.DoEvents();
            try
            {
                RemoteActions.SetLocalUserPassword(_computer, name, password);
                _statusLabel.Text = $"Пароль изменён: {_computer}\\{name}";
                MessageBox.Show(this,
                    $"Пароль для {_computer}\\{name} успешно изменён.",
                    "Сменить пароль", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Ошибка: {ex.Message}";
                MessageBox.Show(this,
                    $"Не удалось сменить пароль для {_computer}\\{name}:\n\n{ex.Message}\n\n" +
                    $"Возможные причины: пароль не соответствует политике сложности;\n" +
                    $"у учётной записи нет прав; аккаунт заблокирован/отключён.",
                    "Сменить пароль", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private sealed class Member
        {
            public string Name { get; set; } = "";
            public string Domain { get; set; } = "";
            public string Type { get; set; } = "";
            public string SID { get; set; } = "";
        }

        private static List<Member> QueryAdmins(string computer)
        {
            var result = new List<Member>();
            try
            {
                var scope = new ManagementScope(new ManagementPath($"\\\\{computer}\\root\\cimv2"),
                    new ConnectionOptions
                    {
                        Timeout = TimeSpan.FromSeconds(8),
                        Authentication = AuthenticationLevel.PacketPrivacy,
                        EnablePrivileges = true
                    });
                scope.Connect();

                // Two-step approach that doesn't rely on the SID property
                // (which is a complex type in WMI and throws "Invalid query"
                // when compared to a string).
                //
                // Step 1: enumerate ALL Win32_GroupUser associations and find
                //         those whose GroupComponent is "Administrators" or
                //         "Администраторы" (localized).
                // Step 2: for each matching association, fetch the user/group
                //         via PartComponent.
                //
                // Slower than a direct query but bulletproof across Windows
                // versions and locales.
                using var assocSearcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT * FROM Win32_GroupUser"));
                using var assocResults = assocSearcher.Get();
                foreach (ManagementObject assoc in assocResults)
                {
                    try
                    {
                        var groupComp = assoc["GroupComponent"] as string ?? "";
                        // groupComp looks like:
                        //   \\HOST\root\cimv2:Win32_Group.Domain="HOST",Name="Administrators"
                        // We match by Name (English or Russian).
                        if (!groupComp.Contains("Name=\"Administrators\"") &&
                            !groupComp.Contains("Name=\"Администраторы\""))
                        {
                            continue;
                        }

                        // PartComponent is the user/group that's a member.
                        var partComp = assoc["PartComponent"] as string ?? "";
                        if (string.IsNullOrEmpty(partComp)) continue;

                        // Resolve the PartComponent path into a ManagementObject
                        // to read its properties.
                        var partPath = new ManagementPath(partComp);
                        using var memberObj = new ManagementObject(scope, partPath, null);
                        try { memberObj.Get(); }
                        catch { continue; }  // member couldn't be loaded

                        var cls = memberObj.ClassPath?.ClassName ?? "";
                        var type = cls == "Win32_Group" ? "Группа" : "Пользователь";
                        result.Add(new Member
                        {
                            Name = memberObj["Name"]?.ToString() ?? "",
                            Domain = memberObj["Domain"]?.ToString() ?? "",
                            Type = type,
                            SID = memberObj["SID"]?.ToString() ?? ""
                        });
                    }
                    catch { /* skip this assoc */ }
                    finally { assoc.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"LocalAdmins query failed on {computer}: {ex.Message}");
            }
            return result;
        }
    }
}
