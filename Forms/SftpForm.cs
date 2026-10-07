using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Renci.SshNet;
using ShadowRDP.Controls;

namespace ShadowRDP.Forms
{
    // SFTP file exchange dialog. Two side-by-side ListViews (local + remote),
    // Total Commander-style header pills, F-key action bar, recursive search
    // on both sides. Uses an existing SftpClient (caller disposes).
    public class SftpForm : Form
    {
        private readonly SftpClient _sftp;

        // v3.3.15 — when called from another SSH session, swap to the new
        // SftpClient + session name without rebuilding the UI. Caller is
        // responsible for disposing the previous client.
        public SftpClient SwapClient(SftpClient newClient, string newSessionName)
        {
            var old = _sftp;
            // Reflection-free swap via field: we cannot reassign readonly
            // Sftp. Instead, expose the change through a property below and
            // route all SFTP operations through the property.
            _currentSftp = newClient;
            var host = newClient.ConnectionInfo.Host;
            Text = string.IsNullOrEmpty(newSessionName)
                ? $"SFTP — обмен файлами   [{host}]"
                : $"SFTP — обмен файлами   [{newSessionName}  ·  {host}]";
            try { _remoteDir = newClient.WorkingDirectory; }
            catch { _remoteDir = "/"; }
            LoadRemote();
            return old;
        }
        private SftpClient _currentSftp = null!;
        private SftpClient Sftp => _currentSftp ?? _sftp;
        private Controls.ScrollHiddenListView _localView = null!;
        private Controls.ScrollHiddenListView _remoteView = null!;
        private TextBox _localPath = null!;
        private TextBox _remotePath = null!;
        private Panel? _localPathPanel;
        private Panel? _remotePathPanel;
        private Label _statusLabel = null!;
        // v3.3.11 — keep references to the bottom-row controls so the Resize
        // handler can reposition them when the form is maximized.
        private Label _localHeader = null!;
        private Label _remoteHeader = null!;
        private Button? _closeBtnRef;
        private readonly List<Button> _fnButtons = new();
        // v3.3.12 — references so RelayoutPanes can move them on resize.
        private Panel? _localSearchPanel;
        private Panel? _remoteSearchPanel;
        private Button? _browseBtn;
        private Button? _remoteHomeBtn;
        private Control? _uploadArrow;
        private Control? _downloadArrow;
        // v3.3.12 — Owner may be null in WM_GETMINMAXINFO (it fires before
        // Load assigns Owner on some Windows versions). Pass the parent form
        // explicitly so we can clamp the maximize bounds.
        private readonly Form? _parentForm;

        private string _localDir = "";
        private string _remoteDir = "";
        // Marked-for-paste remote path (set by "Скопировать" in remote
        // context menu / search results). Ctrl+V in local pane downloads.
        private string? _copySource;
        private static Color RowAlt => IsBlue ? Color.FromArgb(0xF5, 0xF5, 0xF5) : ShadowRDP.Helpers.UiTheme.RowAlt;

        private static Color RowBase => IsBlue ? Color.White : ShadowRDP.Helpers.UiTheme.RowBase;
        private static bool IsBlue => ShadowRDP.Helpers.UiTheme.CurrentName != "Olive";
        private static readonly Color FieldBorder = Color.FromArgb(0xC0, 0xC0, 0xC0);

        public SftpForm(SftpClient sftp, string sessionName = "", Form? parent = null) : base()
        {
            _sftp = sftp;
            _parentForm = parent;

            // v3.3.9 — title shows session name + host so the user knows which
            // server they're talking to when multiple SFTP windows are open.
            var host = sftp.ConnectionInfo.Host;
            Text = string.IsNullOrEmpty(sessionName)
                ? $"SFTP — обмен файлами   [{host}]"
                : $"SFTP — обмен файлами   [{sessionName}  ·  {host}]";
            ClientSize = new Size(900, 580);
            MinimumSize = new Size(700, 480);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            BackColor = Helpers.UiTheme.Background;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = true;  // v3.3.9 — allow maximize

            // === Pane headers + path in ONE row ==============================
            // Header label (20%) + path TextBox (80%) share the same Y.
            _localHeader = MakePaneHeader("Локально", 12);
            _remoteHeader = MakePaneHeader("Сервер", 488);
            Controls.Add(_localHeader);
            Controls.Add(_remoteHeader);

            // === Local path (same row as header) ============================
            var localPathPanel = new Panel
            {
                Location = new Point(92, 18),
                Size = new Size(280, 28),
                BackColor = Color.White
            };
            localPathPanel.Paint += PaintBorder;
            _localPath = new TextBox
            {
                Location = new Point(10, 4),
                Font = new Font("Segoe UI", 10F),
                BorderStyle = BorderStyle.None,
                BackColor = Color.White
            };
            _localPath.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    TryNavigateLocal(_localPath.Text.Trim());
                }
            };
            localPathPanel.Controls.Add(_localPath);
            localPathPanel.Resize += (s, e) =>
            {
                _localPath.Width = localPathPanel.ClientSize.Width - 12;
                _localPath.Height = localPathPanel.ClientSize.Height - 8;
            };
            Controls.Add(localPathPanel);
            _localPathPanel = localPathPanel;

            var browseBtn = new Button
            {
                Text = "…",
                Location = new Point(376, 18),
                Size = new Size(36, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Helpers.UiTheme.Muted,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            browseBtn.Click += (s, e) => BrowseLocalFolder();
            browseBtn.Paint += (s, e) =>
            {
                ControlPaint.DrawBorder(e.Graphics, browseBtn.ClientRectangle,
                    FieldBorder, ButtonBorderStyle.Solid);
            };
            Controls.Add(browseBtn);
            _browseBtn = browseBtn;

            // === Remote path (same row as header) ===========================
            var remotePathPanel = new Panel
            {
                Location = new Point(568, 18),
                Size = new Size(280, 28),
                BackColor = Color.White
            };
            remotePathPanel.Paint += PaintBorder;
            _remotePath = new TextBox
            {
                Location = new Point(10, 4),
                Font = new Font("Segoe UI", 10F),
                BorderStyle = BorderStyle.None,
                BackColor = Color.White
            };
            _remotePath.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    TryNavigateRemote(_remotePath.Text.Trim());
                }
            };
            remotePathPanel.Controls.Add(_remotePath);
            remotePathPanel.Resize += (s, e) =>
            {
                _remotePath.Width = remotePathPanel.ClientSize.Width - 12;
                _remotePath.Height = remotePathPanel.ClientSize.Height - 8;
            };
            Controls.Add(remotePathPanel);
            _remotePathPanel = remotePathPanel;

            var remoteHomeBtn = new Button
            {
                Text = "🏠",
                Location = new Point(852, 18),
                Size = new Size(36, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Helpers.UiTheme.Muted,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 10F)
            };
            remoteHomeBtn.Click += (s, e) =>
            {
                try { TryNavigateRemote(Sftp.WorkingDirectory); } catch { }
            };
            remoteHomeBtn.Paint += (s, e) =>
            {
                ControlPaint.DrawBorder(e.Graphics, remoteHomeBtn.ClientRectangle,
                    FieldBorder, ButtonBorderStyle.Solid);
            };
            Controls.Add(remoteHomeBtn);
            _remoteHomeBtn = remoteHomeBtn;

            // === Search row (both panes) ====================================
            var localSearchPanel = MakeSearchBox("Поиск файла на этом ПК…", out var localSearchBox);
            localSearchPanel.Location = new Point(12, 52);
            localSearchPanel.Size = new Size(302, 24);
            _localSearchPanel = localSearchPanel;
            localSearchBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    SearchLocal(localSearchBox.Text.Trim());
                }
            };
            Controls.Add(localSearchPanel);

            var remoteSearchPanel = MakeSearchBox("Поиск файла на сервере…", out var remoteSearchBox);
            remoteSearchPanel.Location = new Point(488, 52);
            remoteSearchPanel.Size = new Size(302, 24);
            _remoteSearchPanel = remoteSearchPanel;
            remoteSearchBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    SearchRemote(remoteSearchBox.Text.Trim());
                }
            };
            Controls.Add(remoteSearchPanel);

            // === ListViews (both panes, same geometry) ======================
            // v3.3.11 — no Anchor on the ListViews. Resize handler positions
            // them explicitly so they don't overlap the status / F-key row.
            _localView = MakeListView(12);
            _localView.DoubleClick += (s, e) => OnLocalDoubleClick();
            _localView.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                    BuildLocalContextMenu().Show(this, _localView.PointToScreen(new Point(e.X, e.Y)));
            };
            // v3.3.18 — header pill turns darker #1E3A5F when its ListView
            // gets focus, signalling which pane is "active". Default is the
            // brand accent #296AA0 (matches the "Книга"/"Сессии" sidebar tabs).
            // v3.3.19 — same colour tracking for the Search button (matches
            // the active header).
            _localView.Enter += (s, e) =>
            {
                _localHeader.BackColor = ShadowRDP.Helpers.UiTheme.Sidebar;
                _remoteHeader.BackColor = ShadowRDP.Helpers.UiTheme.HeaderBar;
                HighlightArrow(_uploadArrow as Button, true);
                HighlightArrow(_downloadArrow as Button, false);
            };
            _localView.Leave += (s, e) =>
            {
                _localHeader.BackColor = ShadowRDP.Helpers.UiTheme.HeaderBar;
                HighlightArrow(_uploadArrow as Button, false);
            };
            Controls.Add(_localView);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachListView(_localView);

            _remoteView = MakeListView(488);
            _remoteView.DoubleClick += (s, e) => OnRemoteDoubleClick();
            _remoteView.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                    BuildRemoteContextMenu().Show(this, _remoteView.PointToScreen(new Point(e.X, e.Y)));
            };
            _remoteView.Enter += (s, e) =>
            {
                _remoteHeader.BackColor = ShadowRDP.Helpers.UiTheme.Sidebar;
                _localHeader.BackColor = ShadowRDP.Helpers.UiTheme.HeaderBar;
                HighlightArrow(_downloadArrow as Button, true);
                HighlightArrow(_uploadArrow as Button, false);
            };
            _remoteView.Leave += (s, e) =>
            {
                _remoteHeader.BackColor = ShadowRDP.Helpers.UiTheme.HeaderBar;
                HighlightArrow(_downloadArrow as Button, false);
            };
            Controls.Add(_remoteView);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachListView(_remoteView);

            // === Middle transfer arrows (no background, hover → accent-blue) =
            // v3.3.8 — same arrow glyph both directions (was ➤/⬅, looked
            // asymmetric). Now → and ← from the same Unicode block.
            // v3.3.10 — anchored Top only so they stay vertically put on
            // resize instead of drifting.
            // v3.3.13 — arrows shrunk: 64×44 / 28pt → 44×36 / 20pt to match
            // the narrower inter-pane gap.
            // v3.3.14 — flat button style (was plain Label). Hover swaps
            // background to accent-blue + arrow to white.
            // v3.3.15 — 1px border (light gray), arrow centered, hover = light
            // gray background (no accent-blue anymore, per user request).
            // v3.3.16 — bg #F5F0EB (warm muted), hover #FAF7F4 (Card — slightly
            // lighter). Manual OnPaint for pixel-perfect centring of the arrow
            // glyph (TextAlign leaves a small vertical offset in some fonts).
            var uploadBtn = MakeArrowButton("→");
            uploadBtn.Location = new Point(430, 220);
            uploadBtn.Click += (s, e) => UploadSelected();
            Controls.Add(uploadBtn);
            _uploadArrow = uploadBtn;

            var downloadBtn = MakeArrowButton("←");
            downloadBtn.Location = new Point(430, 262);
            downloadBtn.Click += (s, e) => DownloadSelected();
            Controls.Add(downloadBtn);
            _downloadArrow = downloadBtn;

            // === Status label =================================================
            _statusLabel = new Label
            {
                Location = new Point(12, 502),
                Size = new Size(876, 22),
                Font = new Font("Segoe UI", 9F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Helpers.UiTheme.Background,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(_statusLabel);

            // === Function key buttons (F5/F6/F7/F8) + Close ===================
            var fnBack = Color.FromArgb(236, 253, 245);
            var fnFore = Color.FromArgb(5, 150, 105);
            var fnFont = new Font("Segoe UI", 9F);

            var f5 = MakeFnButton("📥  F5  Скачать", fnBack, fnFore, fnFont);
            f5.Location = new Point(12, 540);
            f5.Click += (s, e) => DownloadSelected();
            _fnButtons.Add(f5);
            Controls.Add(f5);

            var f6 = MakeFnButton("📤  F6  Загрузить", fnBack, fnFore, fnFont);
            f6.Location = new Point(132, 540);
            f6.Click += (s, e) => UploadSelected();
            _fnButtons.Add(f6);
            Controls.Add(f6);

            var f7 = MakeFnButton("📁  F7  Папка", fnBack, fnFore, fnFont);
            f7.Location = new Point(252, 540);
            f7.Click += (s, e) => CreateFolder();
            _fnButtons.Add(f7);
            Controls.Add(f7);

            var f8 = MakeFnButton("🗑  F8  Удалить", fnBack, fnFore, fnFont);
            f8.Location = new Point(372, 540);
            f8.Click += (s, e) => DeleteSelected();
            _fnButtons.Add(f8);
            Controls.Add(f8);

            var closeBtn = MakeFnButton("✕  Закрыть (Esc)",
                Color.FromArgb(243, 244, 246), Color.FromArgb(75, 85, 99), fnFont);
            closeBtn.Location = new Point(764, 540);
            closeBtn.Click += (s, e) => Close();
            Controls.Add(closeBtn);
            _closeBtnRef = closeBtn;

            // F5/F6/F7/F8 keyboard shortcuts + Esc closes + Ctrl+V pastes.
            // v3.3.8 — Delete key triggers DeleteSelected() on the focused pane.
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F5) { DownloadSelected(); e.Handled = true; }
                else if (e.KeyCode == Keys.F6) { UploadSelected(); e.Handled = true; }
                else if (e.KeyCode == Keys.F7) { CreateFolder(); e.Handled = true; }
                else if (e.KeyCode == Keys.F8 || e.KeyCode == Keys.Delete)
                {
                    // Only fire on Delete when a ListView has focus (so Backspace
                    // in a TextBox doesn't accidentally nuke files).
                    if (e.KeyCode == Keys.Delete && ActiveControl != _localView && ActiveControl != _remoteView) return;
                    DeleteSelected();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.V && ActiveControl == _localView)
                {
                    PasteCopiedRemoteFile();
                    e.Handled = true;
                }
            };

            Load += (s, e) => Initialize();
            // v3.3.14 — maximize-based clamps (MaximizedBounds, WM_GETMINMAXINFO)
            // did NOT stop Windows from filling the whole screen for this
            // modal dialog. Workaround: intercept the maximize click and
            // explicitly set Bounds = Owner.Bounds with WindowState = Normal.
            // The form LOOKS maximized inside the RDP Manager but never asks
            // Windows to maximize.
            MaximizeBox = false;  // hide the maximize button — we handle it ourselves
            Resize += (s, e) => RelayoutPanes();
            // v3.3.13 — snap to parent's bounds on first show.
            Load += (s, e) =>
            {
                var p = Owner as Form ?? _parentForm;
                if (p != null && p.WindowState != FormWindowState.Minimized)
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = p.Bounds;
                }
            };
            // Custom "развернуть" button top-right corner.
            var maximizeBtn = new Button
            {
                Text = "⛶",
                Size = new Size(30, 22),
                FlatStyle = FlatStyle.Flat,
                BackColor = Helpers.UiTheme.Background,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 10F),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            maximizeBtn.FlatAppearance.BorderSize = 0;
            maximizeBtn.Click += (s, e) =>
            {
                var p = Owner as Form ?? _parentForm;
                if (p != null)
                {
                    if (Bounds == p.Bounds)
                    {
                        // restore to default size
                        Bounds = new Rectangle(
                            p.Left + (p.Width - 900) / 2,
                            p.Top + (p.Height - 580) / 2,
                            900, 580);
                    }
                    else
                    {
                        Bounds = p.Bounds;
                    }
                }
            };
            Controls.Add(maximizeBtn);
            RelayoutPanes();
        }

        // v3.3.11 — full layout. Computes ListView height from ClientSize so
        // the bottom row (status + F-keys + Close) is always visible. Wider
        // windows give each ListView more width too.
        // v3.3.12 — also moves search panels + buttons + path rows + arrows
        // so everything stays in its column. Earlier only the ListViews
        // shifted, which left both search boxes stuck at X=488 / 794 — the
        // remote box ended up in the LEFT half on a wide window.
        private void RelayoutPanes()
        {
            if (_localView == null || _remoteView == null || _statusLabel == null) return;
            var w = ClientSize.Width;
            var h = ClientSize.Height;
            // v3.8.x — gap 50, arrows 40×32 (2px less per side).
            const int margin = 12;
            const int gap = 50;
            const int arrowW = 40;
            var colW = Math.Max(200, (w - 2 * margin - gap) / 2);
            var lvTop = 84;
            var lvBottom = h - 80;
            var lvH = Math.Max(120, lvBottom - lvTop);

            var leftX = margin;
            var rightX = margin + colW + gap;

            _localView.Left = leftX;
            _localView.Top = lvTop;
            _localView.Width = colW;
            _localView.Height = lvH;
            // v3.3.13 — scale column headers horizontally too. Имя ~70% of
            // column width, Размер ~30%.
            // v3.5.1 — subtract vertical-scrollbar width so a populated pane
            // (vertical scroll visible) doesn't trigger a horizontal scroll
            // bar at the bottom. Was leaving 4px which wasn't enough.
            if (_localView.Columns.Count >= 3)
            {
                int avail = colW - SystemInformation.VerticalScrollBarWidth;
                _localView.Columns[0].Width = (int)(avail * 0.50);
                _localView.Columns[1].Width = (int)(avail * 0.25);
                _localView.Columns[2].Width = avail - _localView.Columns[0].Width - _localView.Columns[1].Width;
            }

            _remoteView.Left = rightX;
            _remoteView.Top = lvTop;
            _remoteView.Width = colW;
            _remoteView.Height = lvH;
            if (_remoteView.Columns.Count >= 3)
            {
                int avail = colW - SystemInformation.VerticalScrollBarWidth;
                _remoteView.Columns[0].Width = (int)(avail * 0.50);
                _remoteView.Columns[1].Width = (int)(avail * 0.25);
                _remoteView.Columns[2].Width = avail - _remoteView.Columns[0].Width - _remoteView.Columns[1].Width;
            }

            int headerW = (int)(colW * 0.20);
            _localHeader.Left = leftX;
            _localHeader.Width = headerW;
            _remoteHeader.Left = rightX;
            _remoteHeader.Width = headerW;

            int pathGap = 4;
            int pathBtnW = 40;
            if (_localPathPanel != null)
            {
                _localPathPanel.Left = leftX + headerW + pathGap;
                _localPathPanel.Width = colW - headerW - pathGap - pathBtnW;
            }
            if (_browseBtn != null) _browseBtn.Left = leftX + colW - pathBtnW + 4;

            if (_remotePathPanel != null)
            {
                _remotePathPanel.Left = rightX + headerW + pathGap;
                _remotePathPanel.Width = colW - headerW - pathGap - pathBtnW;
            }
            if (_remoteHomeBtn != null) _remoteHomeBtn.Left = rightX + colW - pathBtnW + 4;

            if (_localSearchPanel != null)
            {
                _localSearchPanel.Left = leftX;
                _localSearchPanel.Width = colW;
            }

            if (_remoteSearchPanel != null)
            {
                _remoteSearchPanel.Left = rightX;
                _remoteSearchPanel.Width = colW;
            }

            // Middle arrows centered in the gap, vertically centered in file panels.
            var midX = leftX + colW + (gap - arrowW) / 2;
            int arrowH = 32, arrowGap = 4;
            int arrowsTotal = arrowH * 2 + arrowGap;
            int arrowY = lvTop + (lvH - arrowsTotal) / 2;
            if (_uploadArrow != null) { _uploadArrow.Left = midX; _uploadArrow.Top = arrowY; }
            if (_downloadArrow != null) { _downloadArrow.Left = midX; _downloadArrow.Top = arrowY + arrowH + arrowGap; }

            // v3.3.14 — keep the maximize button in the top-right corner.
            foreach (Control c in Controls)
            {
                if (c is Button b && b.Text == "⛶")
                {
                    b.Top = 18;
                    b.Left = w - 12 - b.Width;
                    break;
                }
            }

            _statusLabel.Top = h - 78;
            _statusLabel.Left = margin;
            _statusLabel.Width = w - 2 * margin;

            var fY = h - 40;
            for (var i = 0; i < _fnButtons.Count; i++)
            {
                _fnButtons[i].Top = fY;
                _fnButtons[i].Left = margin + i * 120;
            }
            if (_closeBtnRef != null)
            {
                _closeBtnRef.Top = fY;
                _closeBtnRef.Left = w - margin - _closeBtnRef.Width;
            }
        }

        private static void PaintBorder(object? sender, PaintEventArgs e)
        {
            if (sender is not Panel p) return;
            ControlPaint.DrawBorder(e.Graphics, p.ClientRectangle,
                FieldBorder, ButtonBorderStyle.Solid);
        }

        private static void HighlightArrow(Button? btn, bool active)
        {
            if (btn == null) return;
            btn.ForeColor = active ? ArrowActive : ArrowInactive;
            btn.FlatAppearance.BorderColor = ArrowBorder;
            btn.Font = new Font("Segoe UI", active ? 18F : 16F, FontStyle.Bold);
            btn.Invalidate();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        private const int WM_GETMINMAXINFO = 0x0024;

        protected override void WndProc(ref Message m)
        {
            // v3.3.12 — Owner may be null on early WM_GETMINMAXINFO (it fires
            // before Load assigns Owner on some Windows builds). Fall back to
            // the _parentForm field that the caller passed in.
            if (m.Msg == WM_GETMINMAXINFO)
            {
                var p = Owner as Form ?? _parentForm;
                if (p != null)
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
                    mmi.ptMaxSize = new POINT { X = p.Width, Y = p.Height };
                    mmi.ptMaxPosition = new POINT { X = p.Left, Y = p.Top };
                    Marshal.StructureToPtr(mmi, m.LParam, fDeleteOld: true);
                }
            }
            base.WndProc(ref m);
        }

        // ===== Helper builders ============================================

        private static Label MakePaneHeader(string text, int x)
        {
            return new Label
            {
                Text = "  " + text,
                // v3.8.x — header bars aligned with the path-input row (28px)
                // but 1px shorter on top and on bottom: Y=19, height 26.
                Location = new Point(x, 19),
                Size = new Size(400, 26),
                Font = new Font("Segoe UI Semibold", 10F),
                ForeColor = Color.White,
                BackColor = ShadowRDP.Helpers.UiTheme.HeaderBar,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        // v3.3.9 — selection colour used to be system blue (#0078D7). Now
        // a softer pastel (#CCE4F7) via OwnerDraw. DrawItem paints background
        // + first column (icon + name); DrawSubItem paints the remaining
        // columns. Earlier OwnerDraw attempt had a hover bug because it
        // mixed OwnerDraw with default sub-item rendering — this version
        // paints every cell explicitly.
        // v3.9.3BI — выбор #E8ECE8.
        // v3.9.3CK — синяя тема: выбор мониторный.
        private static Color SelectionColor => IsBlue ? Color.FromArgb(0xE9, 0xEF, 0xF6) : Helpers.UiTheme.AccentSoft;

        private ScrollHiddenListView MakeListView(int x)
        {
            var lv = new Controls.ScrollHiddenListView
            {
                Location = new Point(x, 116),
                Size = new Size(400, 378),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                HideSelection = false,
                BackColor = RowBase,
                ForeColor = Helpers.UiTheme.Foreground,
                Font = new Font("Segoe UI", 9F),
                BorderStyle = BorderStyle.None,
                SmallImageList = Helpers.SystemFileIcons.GetImageList(),
                HeaderStyle = ColumnHeaderStyle.Clickable,
                HoverSelection = false,
                HotTracking = false,
                Activation = ItemActivation.Standard,
                OwnerDraw = true
            };
            lv.Columns.Add("Имя", 260);
            lv.Columns.Add("Размер", 110, HorizontalAlignment.Right);
            lv.Columns.Add("Дата изм.", 120, HorizontalAlignment.Left);

            lv.DrawItem += (sender, e) =>
            {
                if (e.Bounds.Width == 0) return;
                var item = e.Item;
                // v3.3.16 — paint BOTH columns in DrawItem so the Size cell
                // is always drawn from the same code path (was disappearing
                // on hover because DrawSubItem lacked access to e.State and
                // Win32 sometimes triggered the default sub-item renderer).
                bool hot = ((int)e.State & 0x40) != 0;
                bool active = item.Selected || hot;
                var bg = active
                    ? SelectionColor
                    : (e.ItemIndex % 2 == 0 ? RowBase : RowAlt);
                using (var b = new SolidBrush(bg))
                    e.Graphics.FillRectangle(b, e.Bounds);
                if (item.ImageIndex >= 0 && lv.SmallImageList != null)
                {
                    lv.SmallImageList.Draw(e.Graphics, e.Bounds.X + 2, e.Bounds.Y + 1, item.ImageIndex);
                }
                // Column 0 — Name (with icon already drawn to the left).
                var nameRect = new Rectangle(e.Bounds.X + 24, e.Bounds.Y,
                    lv.Columns[0].Width - 24, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, item.Text, lv.Font, nameRect,
                    Helpers.UiTheme.Foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                // Column 1 — Size. Only if the column exists.
                if (lv.Columns.Count >= 2 && item.SubItems.Count >= 2)
                {
                    var sizeRect = new Rectangle(
                        e.Bounds.X + lv.Columns[0].Width,
                        e.Bounds.Y,
                        lv.Columns[1].Width,
                        e.Bounds.Height);
                    TextRenderer.DrawText(e.Graphics, item.SubItems[1].Text,
                        lv.Font, sizeRect, Helpers.UiTheme.Foreground,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }

                // Column 2 — Date modified.
                if (lv.Columns.Count >= 3 && item.SubItems.Count >= 3)
                {
                    var dateRect = new Rectangle(
                        e.Bounds.X + lv.Columns[0].Width + lv.Columns[1].Width + 4,
                        e.Bounds.Y,
                        lv.Columns[2].Width - 4,
                        e.Bounds.Height);
                    TextRenderer.DrawText(e.Graphics, item.SubItems[2].Text,
                        lv.Font, dateRect, Helpers.UiTheme.Foreground,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }

                // Column dividers — BorderSubtle to match unified tables.
                using var divPen = new Pen(Helpers.UiTheme.BorderSubtle, 1);
                if (lv.Columns.Count >= 2)
                    e.Graphics.DrawLine(divPen, e.Bounds.X + lv.Columns[0].Width, e.Bounds.Y,
                        e.Bounds.X + lv.Columns[0].Width, e.Bounds.Bottom);
                if (lv.Columns.Count >= 3)
                    e.Graphics.DrawLine(divPen, e.Bounds.X + lv.Columns[0].Width + lv.Columns[1].Width, e.Bounds.Y,
                        e.Bounds.X + lv.Columns[0].Width + lv.Columns[1].Width, e.Bounds.Bottom);
            };
            lv.DrawSubItem += (sender, e) =>
            {
                // v3.3.16 — DrawItem paints everything; DrawSubItem is a
                // no-op so Win32's default sub-item renderer doesn't fight
                // us on hover repaints.
            };
            lv.DrawColumnHeader += (sender, e) =>
            {
                // v3.9.3CK — синяя тема: шапка как у всех таблиц.
                var hb = e.Bounds;
                if (IsBlue)
                {
                    using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(hb,
                        Color.FromArgb(0xE0, 0xE7, 0xF0), Color.FromArgb(0xD3, 0xDC, 0xE8), 90f);
                    e.Graphics.FillRectangle(lg, hb);
                }
                else e.Graphics.FillRectangle(new SolidBrush(Color.White), hb);
                // Manual text draw for precise left alignment (unified white header).
                var textRect = new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
                using var fnt = new Font("Segoe UI Semibold", 9F);
                // v3.9.3AQ — чёткий ClearType заголовка.
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                e.Graphics.DrawString(e.Header!.Text, fnt,
                    new SolidBrush(Helpers.UiTheme.Foreground), textRect,
                    new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center });
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SystemDefault;
            };
            return lv;
        }

        private void StyleItems(ListView lv)
        {
            for (var i = 0; i < lv.Items.Count; i++)
            {
                var item = lv.Items[i];
                item.UseItemStyleForSubItems = true;
                item.BackColor = (i % 2 == 0) ? RowBase : RowAlt;
                item.ForeColor = Helpers.UiTheme.Foreground;
            }
        }

        private static Button MakeFnButton(string text, Color back, Color fore, Font font)
        {
            var btn = new Button
            {
                Text = text,
                Size = new Size(108, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = fore,
                Font = font,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        // v3.3.16 — flat transfer-arrow button. Custom OnPaint centres the
        // arrow glyph exactly (TextAlign + Button leave a small vertical
        // offset that makes the arrow look off-centre). Bg #F5F0EB, hover
        // #FAF7F4, 1px border #D0D0D0.
        private static readonly Color ArrowInactive = ShadowRDP.Helpers.UiTheme.HeaderBar;
        private static readonly Color ArrowActive = ShadowRDP.Helpers.UiTheme.Sidebar;
        private static readonly Color ArrowBorder = Color.FromArgb(0xD9, 0xD9, 0xD9);

        private static Button MakeArrowButton(string arrow)
        {
            var btn = new Button
            {
                Text = "",
                // v3.8.x — 44×36 → 40×32 (2px less on every side) + white bg.
                Size = new Size(40, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = ArrowInactive,
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                Anchor = AnchorStyles.Top
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = ArrowBorder;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xFA, 0xF7, 0xF4);
            btn.Paint += (s, e) =>
            {
                var b = (Button)s!;
                var g = e.Graphics;
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                var rect = new Rectangle(0, -2, b.Width, b.Height);
                using var brush = new SolidBrush(b.ForeColor);
                // v3.9.3AQ — чёткий ClearType стрелки.
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.DrawString(arrow, b.Font, brush, rect, sf);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SystemDefault;
            };
            return btn;
        }

        // v3.3.10 — search box with a leading icon INSIDE the field. Wraps a
        // borderless TextBox in a Panel with FixedSingle border, with the 🔍
        // glyph painted to the left of the input. Returns the wrapper panel;
        // the actual TextBox is exposed via the out parameter so callers can
        // subscribe to KeyDown / read Text.
        // v3.3.13 — TextBox was Dock=Fill which let the placeholder text run
        // under the icon on wide windows. Now positioned explicitly with a
        // left offset (28px) so the placeholder never overlaps the icon.
        private static Panel MakeSearchBox(string placeholder, out TextBox tbOut)
        {
            var panel = new Panel
            {
                BackColor = Color.White
            };
            panel.Paint += PaintBorder;
            var icon = new Label
            {
                Text = "🔍",
                Location = new Point(1, 1),
                Size = new Size(26, 22),
                Font = new Font("Segoe UI", 10F),
                ForeColor = Helpers.UiTheme.MutedForeground,
                BackColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            panel.Controls.Add(icon);
            var tb = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9F),
                PlaceholderText = placeholder,
                Location = new Point(29, 3),
                Size = new Size(panel.Width - 31, 18),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            panel.Controls.Add(tb);
            // Resize handler — captured `tb` is now a local (not an out
            // parameter) so it's safe to use in the lambda.
            panel.Resize += (s, e) =>
            {
                tb.Width = panel.Width - 31;
            };
            tbOut = tb;
            return panel;
        }

        // ===== Init / navigation ==========================================

        private void Initialize()
        {
            _localDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            LoadLocal();
            try { _remoteDir = Sftp.WorkingDirectory; }
            catch { _remoteDir = "/"; }
            LoadRemote();
        }

        private void LoadLocal()
        {
            _localView.BeginUpdate();
            _localView.Items.Clear();
            if (_localPath.Text != _localDir) _localPath.Text = _localDir;
            try
            {
                var parent = Directory.GetParent(_localDir);
                if (parent != null) _localView.Items.Add(CreateItem("..", -1, true));
                var di = new DirectoryInfo(_localDir);
                foreach (var d in di.GetDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    _localView.Items.Add(CreateItem(d.Name, -1, true, d.LastWriteTime));
                foreach (var f in di.GetFiles().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    _localView.Items.Add(CreateItem(f.Name, f.Length, false, f.LastWriteTime));
            }
            catch (Exception ex) { _statusLabel.Text = $"Локальная ошибка: {ex.Message}"; }
            _localView.EndUpdate();
            StyleItems(_localView);
        }

        private void LoadRemote()
        {
            _remoteView.BeginUpdate();
            _remoteView.Items.Clear();
            // v3.3.8 — keep TextBox in sync (don't clobber caret if user is typing).
            if (_remotePath.Text != _remoteDir) _remotePath.Text = _remoteDir;
            try
            {
                _remoteView.Items.Add(CreateItem("..", -1, true));
                foreach (var entry in Sftp.ListDirectory(_remoteDir)
                             .OrderBy(x => !x.IsDirectory)
                             .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (entry.Name == "." || entry.Name == "..") continue;
                    _remoteView.Items.Add(CreateItem(entry.Name, entry.Length, entry.IsDirectory, entry.LastWriteTime));
                }
            }
            catch (Exception ex) { _statusLabel.Text = $"SFTP ошибка: {ex.Message}"; }
            _remoteView.EndUpdate();
            StyleItems(_remoteView);
        }

        private static ListViewItem CreateItem(string name, long size, bool isDir, DateTime? modified = null)
        {
            var item = new ListViewItem(name);
            item.SubItems.Add(isDir ? "" : FormatSize(size));
            item.SubItems.Add(modified.HasValue ? modified.Value.ToString("dd.MM.yyyy HH:mm") : "");
            item.Tag = isDir;
            item.ImageIndex = Helpers.SystemFileIcons.IndexFor(name, isDir);
            return item;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        private void OnLocalDoubleClick()
        {
            if (_localView.SelectedItems.Count == 0) return;
            var item = _localView.SelectedItems[0];
            if (item.Text == "..")
            {
                var parent = Directory.GetParent(_localDir);
                if (parent != null) { _localDir = parent.FullName; LoadLocal(); }
                return;
            }
            if ((bool)(item.Tag ?? false)) { _localDir = Path.Combine(_localDir, item.Text); LoadLocal(); }
            else UploadSelected();  // v3.3.6 — file double-click → upload
        }

        private void OnRemoteDoubleClick()
        {
            if (_remoteView.SelectedItems.Count == 0) return;
            var item = _remoteView.SelectedItems[0];
            if (item.Text == "..")
            {
                var idx = _remoteDir.TrimEnd('/').LastIndexOf('/');
                _remoteDir = idx > 0 ? _remoteDir.Substring(0, idx) : "/";
                LoadRemote();
                return;
            }
            if ((bool)(item.Tag ?? false)) { _remoteDir = _remoteDir.TrimEnd('/') + "/" + item.Text; LoadRemote(); }
            else DownloadSelected();  // v3.3.6 — file double-click → download
        }

        private void TryNavigateLocal(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (!Directory.Exists(path))
                {
                    _statusLabel.Text = $"Путь не найден: {path}";
                    _localPath.Text = _localDir;
                    return;
                }
                _localDir = Path.GetFullPath(path);
                LoadLocal();
                _statusLabel.Text = "";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Не удалось открыть: {ex.Message}";
                _localPath.Text = _localDir;
            }
        }

        private void TryNavigateRemote(string path, string? highlightName = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var attrs = Sftp.GetAttributes(path);
                if (!attrs.IsDirectory) { _statusLabel.Text = $"Не папка: {path}"; return; }
                _remoteDir = path;
                LoadRemote();
                _statusLabel.Text = "";
                if (!string.IsNullOrEmpty(highlightName))
                {
                    var found = _remoteView.FindItemWithText(highlightName);
                    if (found != null)
                    {
                        found.Selected = true;
                        _remoteView.EnsureVisible(found.Index);
                        _remoteView.Focus();
                    }
                }
            }
            catch (Exception ex) { _statusLabel.Text = $"Не удалось открыть: {ex.Message}"; }
        }

        private void BrowseLocalFolder()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Выберите локальную или сетевую папку",
                ShowNewFolderButton = false,
                AutoUpgradeEnabled = true
            };
            try { if (Directory.Exists(_localDir)) dlg.SelectedPath = _localDir; } catch { }
            if (ShadowRDP.Helpers.UiModal.ShowDimmed(this, dlg) == DialogResult.OK) TryNavigateLocal(dlg.SelectedPath);
        }

        // ===== Transfers ==================================================

        private void UploadSelected()
        {
            if (_localView.SelectedItems.Count == 0)
            {
                _statusLabel.Text = "Выберите файлы слева для загрузки.";
                return;
            }
            int ok = 0, fail = 0;
            foreach (ListViewItem item in _localView.SelectedItems)
            {
                if ((bool)(item.Tag ?? false)) continue;
                var localPath = Path.Combine(_localDir, item.Text);
                var remotePath = _remoteDir.TrimEnd('/') + "/" + item.Text;
                try
                {
                    _statusLabel.Text = $"Загрузка {item.Text}…";
                    Application.DoEvents();
                    using var fs = File.OpenRead(localPath);
                    Sftp.UploadFile(fs, remotePath, true);
                    ok++;
                }
                catch (Exception ex) { fail++; Logger.Warn($"SFTP upload '{localPath}': {ex.Message}"); }
            }
            _statusLabel.Text = $"Загружено: {ok}, ошибок: {fail}";
            LoadRemote();
        }

        private void DownloadSelected()
        {
            if (_remoteView.SelectedItems.Count == 0)
            {
                _statusLabel.Text = "Выберите файлы справа для скачивания.";
                return;
            }
            int ok = 0, fail = 0;
            foreach (ListViewItem item in _remoteView.SelectedItems)
            {
                if ((bool)(item.Tag ?? false)) continue;
                var remotePath = _remoteDir.TrimEnd('/') + "/" + item.Text;
                var localPath = Path.Combine(_localDir, item.Text);
                try
                {
                    _statusLabel.Text = $"Скачивание {item.Text}…";
                    Application.DoEvents();
                    using var fs = File.Create(localPath);
                    Sftp.DownloadFile(remotePath, fs);
                    ok++;
                }
                catch (Exception ex) { fail++; Logger.Warn($"SFTP download '{remotePath}': {ex.Message}"); }
            }
            _statusLabel.Text = $"Скачано: {ok}, ошибок: {fail}";
            LoadLocal();
        }

        private void PasteCopiedRemoteFile()
        {
            if (string.IsNullOrEmpty(_copySource))
            {
                _statusLabel.Text = "Буфер пуст. Скопируйте файл через контекстное меню.";
                return;
            }
            var name = Path.GetFileName(_copySource.TrimEnd('/'));
            var localPath = Path.Combine(_localDir, name);
            try
            {
                _statusLabel.Text = $"Скачивание {name}…";
                Application.DoEvents();
                using var fs = File.Create(localPath);
                Sftp.DownloadFile(_copySource, fs);
                _statusLabel.Text = $"Вставлено: {localPath}";
                LoadLocal();
                _copySource = null;
            }
            catch (Exception ex) { _statusLabel.Text = $"Не удалось вставить: {ex.Message}"; }
        }

        // ===== F7 / F8 =====================================================

        private void CreateFolder()
        {
            var name = PromptDialog.Show(this, "Новая папка", "Имя папки:", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim().Replace('/', '_').Replace('\\', '_');
            var focused = ActiveControl == _localView ? _localView : _remoteView;
            try
            {
                if (focused == _localView)
                {
                    var path = Path.Combine(_localDir, name);
                    Directory.CreateDirectory(path);
                    _statusLabel.Text = $"Создано: {path}";
                    LoadLocal();
                }
                else
                {
                    var path = _remoteDir.TrimEnd('/') + "/" + name;
                    Sftp.CreateDirectory(path);
                    _statusLabel.Text = $"Создано: {path}";
                    LoadRemote();
                }
            }
            catch (Exception ex) { _statusLabel.Text = $"Не удалось: {ex.Message}"; }
        }

        private void DeleteSelected()
        {
            var focused = ActiveControl == _localView ? _localView : _remoteView;
            if (focused.SelectedItems.Count == 0)
            {
                _statusLabel.Text = "Нечего удалять.";
                return;
            }
            var side = focused == _localView ? "локальной" : "удалённой";
            if (MessageBox.Show(this,
                    $"Удалить {focused.SelectedItems.Count} элемент(ов) из {side} папки?",
                    "Удалить", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            int ok = 0, fail = 0;
            foreach (ListViewItem item in focused.SelectedItems.Cast<ListViewItem>().ToList())
            {
                try
                {
                    if (focused == _localView)
                    {
                        var path = Path.Combine(_localDir, item.Text);
                        if ((bool)(item.Tag ?? false)) Directory.Delete(path, recursive: true);
                        else File.Delete(path);
                    }
                    else
                    {
                        var path = _remoteDir.TrimEnd('/') + "/" + item.Text;
                        if ((bool)(item.Tag ?? false)) Sftp.DeleteDirectory(path);
                        else Sftp.DeleteFile(path);
                    }
                    ok++;
                }
                catch (Exception ex) { fail++; Logger.Warn($"Delete '{item.Text}': {ex.Message}"); }
            }
            _statusLabel.Text = $"Удалено: {ok}, ошибок: {fail}";
            if (focused == _localView) LoadLocal(); else LoadRemote();
        }

        // ===== Context menus ==============================================

        private Helpers.ThemedContextMenu BuildLocalContextMenu()
        {
            var ctx = new Helpers.ThemedContextMenu();
            ctx.AddHeader("Локальные файлы");
            ctx.AddItem("Копировать путь файла", "copy", () =>
            {
                if (_localView.SelectedItems.Count == 0) return;
                try { Clipboard.SetText(Path.Combine(_localDir, _localView.SelectedItems[0].Text)); } catch { }
                _statusLabel.Text = "Путь скопирован в буфер.";
            });
            ctx.AddItem("Скопировать", "copy", () =>
            {
                if (_localView.SelectedItems.Count == 0) return;
                var files = new System.Collections.Specialized.StringCollection();
                foreach (ListViewItem it in _localView.SelectedItems)
                {
                    if ((bool)(it.Tag ?? false)) continue;
                    files.Add(Path.Combine(_localDir, it.Text));
                }
                if (files.Count == 0) return;
                Clipboard.SetDataObject(new DataObject(DataFormats.FileDrop, files), true);
                _statusLabel.Text = $"Скопировано в буфер: {files.Count} файл(ов).";
            });
            ctx.AddSeparator();
            ctx.AddItem("Переименовать…", "info", () =>
            {
                if (_localView.SelectedItems.Count == 0) return;
                var item = _localView.SelectedItems[0];
                var oldPath = Path.Combine(_localDir, item.Text);
                var newName = PromptDialog.Show(this, "Переименовать", "Новое имя:", item.Text);
                if (string.IsNullOrWhiteSpace(newName) || newName == item.Text) return;
                try
                {
                    var target = Path.Combine(_localDir, newName);
                    if ((bool)(item.Tag ?? false)) Directory.Move(oldPath, target);
                    else File.Move(oldPath, target);
                    _statusLabel.Text = $"Переименовано: {item.Text} → {newName}";
                    LoadLocal();
                }
                catch (Exception ex) { _statusLabel.Text = $"Не удалось: {ex.Message}"; }
            });
            ctx.AddItem("Изменить…", "info", () =>
            {
                if (_localView.SelectedItems.Count == 0) return;
                var item = _localView.SelectedItems[0];
                if ((bool)(item.Tag ?? false)) { _statusLabel.Text = "Нельзя редактировать папку."; return; }
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe",
                        $"\"{Path.Combine(_localDir, item.Text)}\"") { UseShellExecute = false });
                }
                catch (Exception ex) { _statusLabel.Text = $"Не удалось: {ex.Message}"; }
            });
            ctx.AddSeparator();
            ctx.AddItem("Удалить", "delete", () => DeleteSelected());
            return ctx;
        }

        private Helpers.ThemedContextMenu BuildRemoteContextMenu()
        {
            var ctx = new Helpers.ThemedContextMenu();
            ctx.AddHeader("Удалённые файлы");
            ctx.AddItem("Копировать путь файла", "copy", () =>
            {
                if (_remoteView.SelectedItems.Count == 0) return;
                try { Clipboard.SetText(_remoteDir.TrimEnd('/') + "/" + _remoteView.SelectedItems[0].Text); } catch { }
                _statusLabel.Text = "Путь скопирован в буфер.";
            });
            ctx.AddItem("Скопировать", "copy", () =>
            {
                if (_remoteView.SelectedItems.Count == 0) return;
                _copySource = _remoteDir.TrimEnd('/') + "/" + _remoteView.SelectedItems[0].Text;
                _statusLabel.Text = $"Скопировано: {_copySource}. Ctrl+V в левой панели.";
            });
            ctx.AddSeparator();
            ctx.AddItem("Переименовать…", "info", () =>
            {
                if (_remoteView.SelectedItems.Count == 0) return;
                var item = _remoteView.SelectedItems[0];
                var oldPath = _remoteDir.TrimEnd('/') + "/" + item.Text;
                var newName = PromptDialog.Show(this, "Переименовать", "Новое имя:", item.Text);
                if (string.IsNullOrWhiteSpace(newName) || newName == item.Text) return;
                try
                {
                    var target = _remoteDir.TrimEnd('/') + "/" + newName;
                    Sftp.RenameFile(oldPath, target);
                    _statusLabel.Text = $"Переименовано: {item.Text} → {newName}";
                    LoadRemote();
                }
                catch (Exception ex) { _statusLabel.Text = $"Не удалось: {ex.Message}"; }
            });
            ctx.AddItem("Изменить…", "info", () =>
            {
                if (_remoteView.SelectedItems.Count == 0) return;
                var item = _remoteView.SelectedItems[0];
                if ((bool)(item.Tag ?? false)) { _statusLabel.Text = "Нельзя редактировать папку."; return; }
                EditRemoteFile(_remoteDir.TrimEnd('/') + "/" + item.Text);
            });
            ctx.AddSeparator();
            ctx.AddItem("Удалить", "delete", () => DeleteSelected());
            return ctx;
        }

        private void EditRemoteFile(string remotePath)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(remotePath));
            try
            {
                using (var fs = File.Create(tempPath)) Sftp.DownloadFile(remotePath, fs);
                _statusLabel.Text = $"Открыто в редакторе: {remotePath}";
                var psi = new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{tempPath}\"")
                {
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return;
                p.WaitForExit();
                using (var fs = File.OpenRead(tempPath)) Sftp.UploadFile(fs, remotePath, true);
                _statusLabel.Text = $"Сохранено: {remotePath}";
                LoadRemote();
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"Не удалось редактировать: {ex.Message}";
                Logger.Warn($"EditRemoteFile '{remotePath}': {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        // ===== Search =====================================================

        private void SearchRemote(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) { _statusLabel.Text = "Введите имя файла."; return; }
            _statusLabel.Text = $"Поиск «{query}» в {_remoteDir}…";
            Application.DoEvents();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = new List<(string Path, long Size, bool IsDir)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            const int maxResults = 500;
            const int maxSeconds = 5;

            void Walk(string dir)
            {
                if (results.Count >= maxResults || sw.Elapsed.TotalSeconds > maxSeconds) return;
                List<Renci.SshNet.Sftp.ISftpFile> entries;
                try { entries = Sftp.ListDirectory(dir).ToList(); }
                catch (Exception ex) { Logger.Warn($"SFTP search '{dir}': {ex.Message}"); return; }
                foreach (var entry in entries)
                {
                    if (results.Count >= maxResults || sw.Elapsed.TotalSeconds > maxSeconds) return;
                    if (entry.Name == "." || entry.Name == "..") continue;
                    if (entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var full = dir.TrimEnd('/') + "/" + entry.Name;
                        if (seen.Add(full))
                        {
                            results.Add((full, entry.Length, entry.IsDirectory));
                            _statusLabel.Text = $"Поиск: найдено {results.Count}…";
                            Application.DoEvents();
                        }
                    }
                    if (entry.IsDirectory) Walk(dir.TrimEnd('/') + "/" + entry.Name);
                }
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                try { Walk(_remoteDir); } catch (Exception ex) { Logger.Warn($"SFTP search crashed: {ex.Message}"); }
                Invoke(new Action(() =>
                {
                    sw.Stop();
                    if (results.Count == 0) { _statusLabel.Text = $"Ничего не найдено за {sw.Elapsed.TotalSeconds:F1}с."; return; }
                    ShowSearchResults(query, results, sw.Elapsed.TotalSeconds, isRemote: true);
                }));
            });
        }

        private void SearchLocal(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) { _statusLabel.Text = "Введите имя файла."; return; }
            _statusLabel.Text = $"Поиск «{query}» в {_localDir}…";
            Application.DoEvents();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = new List<(string Path, long Size, bool IsDir)>();
            const int maxResults = 500;
            const int maxSeconds = 5;

            void Walk(string dir)
            {
                if (results.Count >= maxResults || sw.Elapsed.TotalSeconds > maxSeconds) return;
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(dir); }
                catch (Exception ex) { Logger.Warn($"Local search '{dir}': {ex.Message}"); return; }
                foreach (var full in entries)
                {
                    if (results.Count >= maxResults || sw.Elapsed.TotalSeconds > maxSeconds) return;
                    var name = Path.GetFileName(full);
                    bool isDir = Directory.Exists(full);
                    if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        long size = 0;
                        try { if (!isDir) size = new FileInfo(full).Length; } catch { }
                        results.Add((full, size, isDir));
                        _statusLabel.Text = $"Поиск: найдено {results.Count}…";
                        Application.DoEvents();
                    }
                    if (isDir) Walk(full);
                }
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                try { Walk(_localDir); } catch (Exception ex) { Logger.Warn($"Local search crashed: {ex.Message}"); }
                Invoke(new Action(() =>
                {
                    sw.Stop();
                    if (results.Count == 0) { _statusLabel.Text = $"Ничего не найдено за {sw.Elapsed.TotalSeconds:F1}с."; return; }
                    ShowSearchResults(query, results, sw.Elapsed.TotalSeconds, isRemote: false);
                }));
            });
        }

        private void ShowSearchResults(string query, List<(string Path, long Size, bool IsDir)> results, double seconds, bool isRemote)
        {
            using var dlg = new Form
            {
                Text = $"Поиск «{query}»: {results.Count} результат(ов) за {seconds:F1}с",
                ClientSize = new Size(640, 420),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                BackColor = Helpers.UiTheme.Background,
                Font = new Font("Segoe UI", 9F)
            };

            var lv = new Controls.ScrollHiddenListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BackColor = Helpers.UiTheme.RowBase,
                ForeColor = Helpers.UiTheme.Foreground,
                Font = new Font("Segoe UI", 9F),
                BorderStyle = BorderStyle.None,
                OwnerDraw = true,
                SmallImageList = Helpers.SystemFileIcons.GetImageList()
            };
            lv.Columns.Add("Путь", 460);
            lv.Columns.Add("Размер", 120, HorizontalAlignment.Right);
            lv.DrawColumnHeader += (sender, e) =>
            {
                e.Graphics.FillRectangle(new SolidBrush(Color.White), e.Bounds);
                var textRect = new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
                using var fnt = new Font("Segoe UI Semibold", 9F);
                // v3.9.3AQ — чёткий ClearType заголовка.
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                e.Graphics.DrawString(e.Header!.Text, fnt,
                    new SolidBrush(Helpers.UiTheme.Foreground), textRect,
                    new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center });
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SystemDefault;
            };
            lv.DrawItem += (sender, e) => { e.DrawDefault = true; };
            lv.DrawSubItem += (sender, e) => { e.DrawDefault = true; };

            for (var i = 0; i < results.Count; i++)
            {
                var r = results[i];
                var item = new ListViewItem(r.Path);
                item.SubItems.Add(r.IsDir ? "" : FormatSize(r.Size));
                item.Tag = r.IsDir;
                item.ImageIndex = Helpers.SystemFileIcons.IndexFor(r.Path, r.IsDir);
                item.BackColor = (i % 2 == 0) ? Helpers.UiTheme.RowBase : Helpers.UiTheme.RowAlt;
                item.ForeColor = Helpers.UiTheme.Foreground;
                lv.Items.Add(item);
            }

            lv.DoubleClick += (s, e) =>
            {
                if (lv.SelectedItems.Count == 0) return;
                var path = lv.SelectedItems[0].Text;
                var isDir = (bool)(lv.SelectedItems[0].Tag ?? false);
                if (isRemote) NavigateToRemoteHit(path, isDir);
                else NavigateToLocalHit(path, isDir);
                dlg.Close();
            };

            // Same context menu shape as the panes: Копировать путь / Скопировать /
            // Переименовать / Изменить / Удалить. "Показать расположение" replaced
            // by "Копировать путь" per user request.
            var navMenu = new Helpers.ThemedContextMenu();
            navMenu.AddHeader("Навигация");
            navMenu.AddItem("Копировать путь", "copy", () =>
            {
                if (lv.SelectedItems.Count == 0) return;
                try { Clipboard.SetText(lv.SelectedItems[0].Text); } catch { }
                _statusLabel.Text = $"Путь скопирован: {lv.SelectedItems[0].Text}";
            });
            navMenu.AddItem("Скопировать", "copy", () =>
            {
                if (lv.SelectedItems.Count == 0) return;
                var path = lv.SelectedItems[0].Text;
                if (isRemote)
                {
                    _copySource = path;
                    _statusLabel.Text = $"Скопировано: {path}. Ctrl+V в левой панели.";
                }
                else
                {
                    var files = new System.Collections.Specialized.StringCollection { path };
                    Clipboard.SetDataObject(new DataObject(DataFormats.FileDrop, files), true);
                    _statusLabel.Text = "Скопировано в буфер Windows.";
                }
                dlg.Close();
            });
            navMenu.AddSeparator();
            navMenu.AddItem("Переименовать…", "info", () =>
            {
                if (lv.SelectedItems.Count == 0) return;
                var path = lv.SelectedItems[0].Text;
                var newName = PromptDialog.Show(this, "Переименовать", "Новое имя:",
                    isRemote ? Path.GetFileName(path.TrimEnd('/')) : Path.GetFileName(path));
                if (string.IsNullOrWhiteSpace(newName)) return;
                try
                {
                    if (isRemote)
                    {
                        var parent = Path.GetDirectoryName(path.Replace('/', '\\'))?.Replace('\\', '/') ?? "/";
                        Sftp.RenameFile(path, parent.TrimEnd('/') + "/" + newName);
                    }
                    else
                    {
                        var target = Path.Combine(Path.GetDirectoryName(path) ?? "", newName);
                        if (Directory.Exists(path)) Directory.Move(path, target);
                        else File.Move(path, target);
                    }
                    _statusLabel.Text = $"Переименовать: {newName}";
                    dlg.Close();
                }
                catch (Exception ex) { _statusLabel.Text = $"Не удалось: {ex.Message}"; }
            });
            navMenu.AddItem("Изменить…", "info", () =>
            {
                if (lv.SelectedItems.Count == 0) return;
                var path = lv.SelectedItems[0].Text;
                if ((bool)(lv.SelectedItems[0].Tag ?? false)) { _statusLabel.Text = "Нельзя редактировать папку."; return; }
                if (isRemote) EditRemoteFile(path);
                else
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = false }); }
                    catch (Exception ex) { _statusLabel.Text = $"Не удалось: {ex.Message}"; }
                }
                dlg.Close();
            });
            navMenu.AddSeparator();
            navMenu.AddItem("Удалить", "delete", () =>
            {
                if (lv.SelectedItems.Count == 0) return;
                var path = lv.SelectedItems[0].Text;
                if (MessageBox.Show(dlg, $"Удалить {path}?", "Удалить",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                try
                {
                    if (isRemote)
                    {
                        if ((bool)(lv.SelectedItems[0].Tag ?? false)) Sftp.DeleteDirectory(path);
                        else Sftp.DeleteFile(path);
                        LoadRemote();
                    }
                    else
                    {
                        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                        else File.Delete(path);
                        LoadLocal();
                    }
                    _statusLabel.Text = $"Удалено: {path}";
                    dlg.Close();
                }
                catch (Exception ex) { _statusLabel.Text = $"Не удалось: {ex.Message}"; }
            });
            lv.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                    navMenu.Show(dlg, lv.PointToScreen(new Point(e.X, e.Y)));
            };

            dlg.Controls.Add(lv);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachListView(lv);
            ShadowRDP.Helpers.UiModal.ShowDimmed(this, dlg);
        }

        private void NavigateToRemoteHit(string path, bool isDir)
        {
            var target = isDir ? path : Path.GetDirectoryName(path.Replace('/', '\\'))?.Replace('\\', '/');
            if (string.IsNullOrEmpty(target)) return;
            var hl = isDir ? Path.GetFileName(path.TrimEnd('/')) : Path.GetFileName(path);
            TryNavigateRemote(target, hl);
        }

        private void NavigateToLocalHit(string path, bool isDir)
        {
            var target = isDir ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(target)) return;
            TryNavigateLocal(target);
            if (!isDir)
            {
                var name = Path.GetFileName(path);
                var found = _localView.FindItemWithText(name);
                if (found != null) { found.Selected = true; _localView.EnsureVisible(found.Index); _localView.Focus(); }
            }
        }
    }
}
