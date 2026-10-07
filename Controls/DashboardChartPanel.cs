using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ShadowRDP.Helpers;
using ShadowRDP.Models;
using Label = System.Windows.Forms.Label;
using Color = System.Drawing.Color;

namespace ShadowRDP.Controls
{
    public class DashboardChartPanel : UserControl
    {
        private readonly TableLayoutPanel _gridCharts;
        private readonly Panel _pnlLevel;
        private readonly Panel _pnlHost;
        private readonly Panel _pnlSource;
        private readonly Panel _pnlTime;
        private readonly Label _lblKpi;
        private DashboardStats? _lastStats;

        private List<string> _hostKeys = new();
        private List<string> _sourceKeys = new();
        private List<DateTime> _timeBuckets = new();

        public event Action<string, string>? OnChartClick;

        public DashboardChartPanel()
        {
            Dock = DockStyle.Top;
            Height = 380;
            BackColor = UiTheme.Card;
            Padding = new Padding(8, 6, 8, 6);

            _lblKpi = new Label
            {
                Dock = DockStyle.Top,
                Height = 18,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = ControlPaint.Dark(UiTheme.MutedForeground, 0.4f),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0)
            };
            Controls.Add(_lblKpi);

            _gridCharts = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = UiTheme.Card,
                Padding = new Padding(0, 4, 0, 0)
            };
            _gridCharts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            _gridCharts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            _gridCharts.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            _gridCharts.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            _pnlLevel = CreateCardPanel("Уровень (клик → фильтр)");
            _pnlHost = CreateCardPanel("Топ хостов");
            _pnlSource = CreateCardPanel("Топ источников");
            _pnlTime = CreateCardPanel("Динамика по времени");

            _gridCharts.Controls.Add(_pnlLevel, 0, 0);
            _gridCharts.Controls.Add(_pnlHost, 1, 0);
            _gridCharts.Controls.Add(_pnlSource, 0, 1);
            _gridCharts.Controls.Add(_pnlTime, 1, 1);

            Controls.Add(_gridCharts);
            Controls.SetChildIndex(_lblKpi, 0);
            Controls.SetChildIndex(_gridCharts, 1);
        }

        private Panel CreateCardPanel(string title)
        {
            var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(4), Margin = new Padding(6) };
            card.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var pen = new Pen(UiTheme.BorderSubtle, 1);
                var r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
                using var path = UiButtons.RoundedRect(r, 6);
                e.Graphics.DrawPath(pen, path);
            };
            var lbl = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 18,
                Font = new Font("Segoe UI Semibold", 8f),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(0x62, 0x6F, 0x5E),
                TextAlign = ContentAlignment.MiddleCenter
            };
            var inner = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(6) };
            inner.Tag = title;
            card.Controls.Add(inner);
            card.Controls.Add(lbl);
            lbl.BringToFront();
            inner.Click += (s, e) => HandleCardClick(card, inner, title);
            lbl.Click += (s, e) => HandleCardClick(card, inner, title);
            return card;
        }

        private void HandleCardClick(Panel card, Panel inner, string title)
        {
            if (title.Contains("Уровень") && _lastStats != null && _lastStats.Total > 0)
            {
                string key = _lastStats.Critical >= _lastStats.Error ? "Critical" : "Error";
                OnChartClick?.Invoke("Level", key);
            }
            else if (title.Contains("хостов") && _hostKeys.Count > 0)
            {
                OnChartClick?.Invoke("Host", _hostKeys[0]);
            }
            else if (title.Contains("источников") && _sourceKeys.Count > 0)
            {
                OnChartClick?.Invoke("Source", _sourceKeys[0]);
            }
            else if (title.Contains("Динамика") && _timeBuckets.Count > 0)
            {
                OnChartClick?.Invoke("Time", _timeBuckets[0].ToString("yyyy-MM-dd HH:mm"));
            }
        }

        public void Update(DashboardStats stats)
        {
            _lastStats = stats;
            double avg = 0;
            if (stats.TimeSeries.Count > 0)
            {
                double hours = stats.Period == "30 мин" ? 0.5 : stats.Period == "1 час" ? 1 : stats.Period == "24 часа" ? 24 : 48;
                avg = Math.Round((double)stats.Total / Math.Max(1, hours), 1);
            }
            _lblKpi.Text = $"Всего: {stats.Total}  •  Critical: {stats.Critical}  •  Error: {stats.Error}  •  Хостов: {stats.ByHost.Count}  •  Ср/час: {avg}";
            UpdateLevel(stats);
            UpdateHost(stats);
            UpdateSource(stats);
            UpdateTime(stats);
        }

        private Panel GetInner(Panel card) => card.Controls.OfType<Panel>().FirstOrDefault(p => p.Tag != null) ?? card;

        private void UpdateLevel(DashboardStats s)
        {
            var inner = GetInner(_pnlLevel);
            inner.Controls.Clear();
            if (s.Total == 0)
            {
                inner.Controls.Add(new Label { Text = "Уровень — нет данных", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = UiTheme.MutedForeground, Font = new Font("Segoe UI", 9f) });
                return;
            }
            var lbl = new Label { Text = $"Уровень ({s.Total})", Dock = DockStyle.Top, Height = 16, Font = new Font("Segoe UI", 7f, FontStyle.Bold), ForeColor = UiTheme.MutedForeground, TextAlign = ContentAlignment.MiddleCenter };
            inner.Controls.Add(lbl);

            var donut = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(8) };
            donut.Paint += (ss, ee) =>
            {
                ee.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var g = ee.Graphics;
                int size = Math.Min(donut.Width, donut.Height) - 16;
                var rect = new Rectangle((donut.Width - size) / 2, (donut.Height - size) / 2, size, size);
                double total = s.Critical + s.Error;
                if (total == 0) total = 1;
                float start = -90;
                float sweepCrit = (float)(s.Critical / total * 360);
                float sweepErr = 360 - sweepCrit;
                using var brCrit = new SolidBrush(Color.FromArgb(176, 122, 114));
                using var brErr = new SolidBrush(Color.FromArgb(41, 106, 160));
                g.FillPie(brCrit, rect, start, sweepCrit);
                g.FillPie(brErr, rect, start + sweepCrit, sweepErr);
                var innerRect = new Rectangle(rect.X + size / 4, rect.Y + size / 4, size / 2, size / 2);
                using var brWhite = new SolidBrush(Color.White);
                g.FillEllipse(brWhite, innerRect);
                // labels inside donut hole
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                // v3.9.3AQ — чёткий ClearType цифры (голый DrawString пикселил).
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.DrawString($"{s.Critical}", new Font("Segoe UI", 7f, FontStyle.Bold), Brushes.Black, innerRect, sf);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SystemDefault;
            };
            donut.Click += (ss, ee) => OnChartClick?.Invoke("Level", s.Critical >= s.Error ? "Critical" : "Error");
            inner.Controls.Add(donut);

            var legend = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 18, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.White };
            legend.Controls.Add(new Label { Text = $"● Critical {s.Critical}", AutoSize = true, Font = new Font("Segoe UI", 7f), ForeColor = Color.FromArgb(176, 122, 114), Margin = new Padding(4, 0, 4, 0) });
            legend.Controls.Add(new Label { Text = $"● Error {s.Error}", AutoSize = true, Font = new Font("Segoe UI", 7f), ForeColor = Color.FromArgb(41, 106, 160), Margin = new Padding(4, 0, 4, 0) });
            inner.Controls.Add(legend);
        }

        private void UpdateHost(DashboardStats s)
        {
            var inner = GetInner(_pnlHost);
            inner.Controls.Clear();
            _hostKeys = s.ByHost.Select(x => x.Key).ToList();
            if (_hostKeys.Count == 0)
            {
                inner.Controls.Add(new Label { Text = "Хосты — нет данных", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = UiTheme.MutedForeground });
                return;
            }
            var lbl = new Label { Text = $"Хосты топ-{_hostKeys.Count}", Dock = DockStyle.Top, Height = 16, Font = new Font("Segoe UI", 7f, FontStyle.Bold), ForeColor = UiTheme.MutedForeground, TextAlign = ContentAlignment.MiddleCenter };
            inner.Controls.Add(lbl);

            var list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.White, Padding = new Padding(4, 2, 4, 2) };
            double max = s.ByHost.Max(x => x.Count);
            if (max == 0) max = 1;
            foreach (var item in s.ByHost)
            {
                var row = new Panel { Height = 18, Dock = DockStyle.Top, BackColor = Color.White, Margin = new Padding(0, 2, 0, 2) };
                var barBg = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(246, 241, 231), Margin = new Padding(70, 4, 30, 4) };
                var barFill = new Panel { Dock = DockStyle.Left, Width = (int)(70 + (barBg.Width - 100) * item.Count / max), BackColor = Color.FromArgb(41, 106, 160) };
                // Actually compute width relative to panel: use barBg width
                barFill.Width = (int)(barBg.Width * item.Count / max);
                barBg.Controls.Add(barFill);
                var lblName = new Label { Text = item.Key.Length > 12 ? item.Key.Substring(0, 12) + "…" : item.Key, AutoSize = false, Width = 68, Dock = DockStyle.Left, Font = new Font("Segoe UI", 7f), ForeColor = Color.FromArgb(51, 65, 85), TextAlign = ContentAlignment.MiddleLeft };
                var lblVal = new Label { Text = item.Count.ToString(), AutoSize = false, Width = 28, Dock = DockStyle.Right, Font = new Font("Consolas", 7f), ForeColor = Color.FromArgb(41, 106, 160), TextAlign = ContentAlignment.MiddleRight };
                row.Controls.Add(barBg);
                row.Controls.Add(lblVal);
                row.Controls.Add(lblName);
                string key = item.Key;
                row.Click += (ss, ee) => OnChartClick?.Invoke("Host", key);
                lblName.Click += (ss, ee) => OnChartClick?.Invoke("Host", key);
                list.Controls.Add(row);
            }
            inner.Controls.Add(list);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachPanel(list);
        }

        private void UpdateSource(DashboardStats s)
        {
            var inner = GetInner(_pnlSource);
            inner.Controls.Clear();
            _sourceKeys = s.BySource.Select(x => x.Key).ToList();
            if (_sourceKeys.Count == 0)
            {
                inner.Controls.Add(new Label { Text = "Источники — нет данных", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = UiTheme.MutedForeground });
                return;
            }
            var lbl = new Label { Text = $"Источники топ-{_sourceKeys.Count}", Dock = DockStyle.Top, Height = 16, Font = new Font("Segoe UI", 7f, FontStyle.Bold), ForeColor = UiTheme.MutedForeground, TextAlign = ContentAlignment.MiddleCenter };
            inner.Controls.Add(lbl);

            var list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.White, Padding = new Padding(4, 2, 4, 2) };
            double max = s.BySource.Max(x => x.Count);
            if (max == 0) max = 1;
            foreach (var item in s.BySource)
            {
                var row = new Panel { Height = 18, Dock = DockStyle.Top, BackColor = Color.White, Margin = new Padding(0, 2, 0, 2) };
                var barBg = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(246, 241, 231), Margin = new Padding(70, 4, 30, 4) };
                var barFill = new Panel { Dock = DockStyle.Left, Width = (int)(100 * item.Count / max), BackColor = Color.FromArgb(245, 158, 11) };
                barFill.Width = (int)(barBg.Width * item.Count / max);
                barBg.Controls.Add(barFill);
                var lblName = new Label { Text = item.Key.Length > 14 ? item.Key.Substring(0, 14) + "…" : item.Key, AutoSize = false, Width = 68, Dock = DockStyle.Left, Font = new Font("Segoe UI", 7f), ForeColor = Color.FromArgb(51, 65, 85) };
                var lblVal = new Label { Text = item.Count.ToString(), AutoSize = false, Width = 28, Dock = DockStyle.Right, Font = new Font("Consolas", 7f), ForeColor = Color.FromArgb(245, 158, 11), TextAlign = ContentAlignment.MiddleRight };
                row.Controls.Add(barBg);
                row.Controls.Add(lblVal);
                row.Controls.Add(lblName);
                string key = item.Key;
                row.Click += (ss, ee) => OnChartClick?.Invoke("Source", key);
                list.Controls.Add(row);
            }
            inner.Controls.Add(list);
            // v4.4.0 — своя тонкая полоса вместо системной.
            ShadowScroll.AttachPanel(list);
        }

        private void UpdateTime(DashboardStats s)
        {
            var inner = GetInner(_pnlTime);
            inner.Controls.Clear();
            _timeBuckets = s.TimeSeries.Select(x => x.Bucket).ToList();
            if (_timeBuckets.Count == 0)
            {
                inner.Controls.Add(new Label { Text = "Динамика — нет данных", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = UiTheme.MutedForeground });
                return;
            }
            var lbl = new Label { Text = $"Динамика ({s.Period})", Dock = DockStyle.Top, Height = 16, Font = new Font("Segoe UI", 7f, FontStyle.Bold), ForeColor = UiTheme.MutedForeground, TextAlign = ContentAlignment.MiddleCenter };
            inner.Controls.Add(lbl);

            var chart = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(4) };
            chart.Paint += (ss, ee) =>
            {
                ee.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int w = chart.Width, h = chart.Height;
                int n = s.TimeSeries.Count;
                if (n == 0) return;
                double max = s.TimeSeries.Max(x => x.Count);
                if (max == 0) max = 1;
                var pts = new PointF[n];
                for (int i = 0; i < n; i++)
                {
                    float x = (float)i / Math.Max(1, n - 1) * (w - 10) + 5;
                    float y = h - 14 - (float)(s.TimeSeries[i].Count / max * (h - 24));
                    pts[i] = new PointF(x, y);
                }
                // grid
                using var penGrid = new Pen(Color.FromArgb(232, 221, 208), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                for (int i = 0; i < 3; i++)
                {
                    int y = 8 + i * (h - 24) / 3;
                    ee.Graphics.DrawLine(penGrid, 5, y, w - 5, y);
                }
                // line + dots
                using var pen = new Pen(Color.FromArgb(240, 160, 80), 2.5f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
                if (n > 1) ee.Graphics.DrawLines(pen, pts);
                foreach (var p in pts)
                {
                    ee.Graphics.FillEllipse(new SolidBrush(Color.FromArgb(240, 160, 80)), p.X - 3, p.Y - 3, 6, 6);
                    ee.Graphics.DrawEllipse(new Pen(Color.White, 1), p.X - 3, p.Y - 3, 6, 6);
                }
                // x labels
                int step = Math.Max(1, n / 4);
                using var fnt = new Font("Consolas", 6f);
                for (int i = 0; i < n; i += step)
                {
                    string lab = s.TimeSeries[i].Bucket.ToString(s.Period == "30 мин" || s.Period == "1 час" ? "HH:mm" : "dd HH:mm");
                    // v3.9.3AQ — чёткий ClearType подписей.
                    ee.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    ee.Graphics.DrawString(lab, fnt, Brushes.Gray, pts[i].X - 12, h - 10);
                    ee.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SystemDefault;
                }
            };
            chart.Click += (ss, ee) =>
            {
                if (_timeBuckets.Count > 0) OnChartClick?.Invoke("Time", _timeBuckets[0].ToString("yyyy-MM-dd HH:mm"));
            };
            inner.Controls.Add(chart);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            try { Invalidate(true); } catch { }
        }
    }
}
