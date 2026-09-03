using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ScottPlot.WinForms;
using ShadowRDP.Helpers;
using ShadowRDP.Models;
using Label = System.Windows.Forms.Label;
using Color = System.Drawing.Color;

namespace ShadowRDP.Controls
{
    public class DashboardChartPanel : UserControl
    {
        private readonly TableLayoutPanel _gridCharts;
        private readonly FormsPlot _plotLevel;
        private readonly FormsPlot _plotHost;
        private readonly FormsPlot _plotSource;
        private readonly FormsPlot _plotTime;
        private readonly Label _lblKpi;
        private DashboardStats? _lastStats;

        // For hit-testing
        private List<string> _hostKeys = new();
        private List<string> _sourceKeys = new();
        private List<DateTime> _timeBuckets = new();

        public event Action<string, string>? OnChartClick; // kind, key

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

            _plotLevel = CreatePlot("Уровень");
            _plotHost = CreatePlot("По хостам");
            _plotSource = CreatePlot("Топ источников");
            _plotTime = CreatePlot("Динамика");

            _gridCharts.Controls.Add(WrapPlot(_plotLevel, "Уровень (клик → фильтр)"), 0, 0);
            _gridCharts.Controls.Add(WrapPlot(_plotHost, "Топ хостов"), 1, 0);
            _gridCharts.Controls.Add(WrapPlot(_plotSource, "Топ источников"), 0, 1);
            _gridCharts.Controls.Add(WrapPlot(_plotTime, "Динамика по времени"), 1, 1);

            Controls.Add(_gridCharts);
            // Ensure kpi on top
            Controls.SetChildIndex(_lblKpi, 0);
            Controls.SetChildIndex(_gridCharts, 1);
        }

        private Panel WrapPlot(FormsPlot plot, string title)
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(4), Margin = new Padding(6) };
            p.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var pen = new Pen(UiTheme.BorderSubtle, 1);
                var r = new Rectangle(0, 0, p.Width - 1, p.Height - 1);
                using var path = UiButtons.RoundedRect(r, 6);
                e.Graphics.DrawPath(pen, path);
            };
            var lbl = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 18,
                Font = new Font("Segoe UI Semibold", 9f),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(0x62, 0x6F, 0x5E),
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(0, 0, 0, 0)
            };
            // Round top of header via parent paint, keep label flat
            plot.Dock = DockStyle.Fill;
            plot.BackColor = Color.White;
            p.Controls.Add(plot);
            p.Controls.Add(lbl);
            return p;
        }

        private FormsPlot CreatePlot(string title)
        {
            var fp = new FormsPlot { Dock = DockStyle.Fill, BackColor = Color.White };
            fp.Plot.FigureBackground.Color = new ScottPlot.Color(255, 255, 255);
            fp.Plot.DataBackground.Color = new ScottPlot.Color(255, 255, 255);
            fp.Plot.Axes.Color(new ScottPlot.Color(UiTheme.BorderSubtle.R, UiTheme.BorderSubtle.G, UiTheme.BorderSubtle.B));
            fp.Plot.Grid.MajorLineColor = new ScottPlot.Color(UiTheme.BorderSubtle.R, UiTheme.BorderSubtle.G, UiTheme.BorderSubtle.B, 60);
            var darkMuted = ControlPaint.Dark(UiTheme.MutedForeground, 0.4f);
            fp.Plot.Axes.Bottom.TickLabelStyle.ForeColor = new ScottPlot.Color(darkMuted.R, darkMuted.G, darkMuted.B);
            fp.Plot.Axes.Left.TickLabelStyle.ForeColor = new ScottPlot.Color(darkMuted.R, darkMuted.G, darkMuted.B);
            fp.Plot.Title(title, 11);
            // Hook click
            fp.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                HandlePlotClick(fp, e.Location);
            };
            return fp;
        }

        private void HandlePlotClick(FormsPlot fp, Point pt)
        {
            if (fp == _plotLevel && _lastStats != null)
            {
                // Pie: simple top/bottom half for Critical/Error
                var rect = fp.ClientRectangle;
                bool isLeft = pt.X < rect.Width / 2;
                // For 2 slices, left vs right is not accurate but give toggle
                // Use X to pick: if pie has 2 slices, pick larger side
                string key = _lastStats.Critical >= _lastStats.Error ? (isLeft ? "Critical" : "Error") : (isLeft ? "Error" : "Critical");
                // Better: pick by total proportion not needed; just toggle between
                if (_lastStats.Total == 0) return;
                // Determine which slice is under cursor by angle (approx)
                // Fallback: if click left half -> Critical else Error
                OnChartClick?.Invoke("Level", key);
            }
            else if (fp == _plotHost && _hostKeys.Count > 0)
            {
                // Horizontal bars: Y determines index
                var plot = fp.Plot;
                var coord = plot.GetCoordinates(new ScottPlot.Pixel(pt.X, pt.Y));
                // Bars are horizontal, Y is index
                int idx = (int)Math.Round(coord.Y);
                if (idx >= 0 && idx < _hostKeys.Count)
                    OnChartClick?.Invoke("Host", _hostKeys[idx]);
            }
            else if (fp == _plotSource && _sourceKeys.Count > 0)
            {
                var plot = fp.Plot;
                var coord = plot.GetCoordinates(new ScottPlot.Pixel(pt.X, pt.Y));
                int idx = (int)Math.Round(coord.Y);
                if (idx >= 0 && idx < _sourceKeys.Count)
                    OnChartClick?.Invoke("Source", _sourceKeys[idx]);
            }
            else if (fp == _plotTime && _timeBuckets.Count > 0)
            {
                var plot = fp.Plot;
                var coord = plot.GetCoordinates(new ScottPlot.Pixel(pt.X, pt.Y));
                int idx = (int)Math.Round(coord.X);
                if (idx >= 0 && idx < _timeBuckets.Count)
                {
                    string key = _timeBuckets[idx].ToString("yyyy-MM-dd HH:mm");
                    OnChartClick?.Invoke("Time", key);
                }
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

        private void StylePlot(ScottPlot.Plot plot)
        {
            plot.FigureBackground.Color = new ScottPlot.Color(255, 255, 255);
            plot.DataBackground.Color = new ScottPlot.Color(255, 255, 255);
            plot.Axes.Color(new ScottPlot.Color(UiTheme.BorderSubtle.R, UiTheme.BorderSubtle.G, UiTheme.BorderSubtle.B));
            plot.Grid.MajorLineColor = new ScottPlot.Color(UiTheme.BorderSubtle.R, UiTheme.BorderSubtle.G, UiTheme.BorderSubtle.B, 60);
            var dm = ControlPaint.Dark(UiTheme.MutedForeground, 0.4f);
            plot.Axes.Bottom.TickLabelStyle.ForeColor = new ScottPlot.Color(dm.R, dm.G, dm.B);
            plot.Axes.Left.TickLabelStyle.ForeColor = new ScottPlot.Color(dm.R, dm.G, dm.B);
            plot.Axes.Bottom.TickLabelStyle.FontSize = 9;
            plot.Axes.Left.TickLabelStyle.FontSize = 9;
        }

        private void UpdateLevel(DashboardStats s)
        {
            var plot = _plotLevel.Plot;
            plot.Clear();
            StylePlot(plot);
            if (s.Total == 0)
            {
                plot.Title("Уровень — нет данных", 10);
                _plotLevel.Refresh();
                return;
            }
            double[] values = { s.Critical, s.Error };
            string[] labels = { $"Critical {s.Critical}", $"Error {s.Error}" };
            var pie = plot.Add.Pie(values);
            for (int i = 0; i < pie.Slices.Count; i++)
            {
                pie.Slices[i].Label = labels[i];
                pie.Slices[i].LabelFontSize = 9;
                pie.Slices[i].LabelFontColor = new ScottPlot.Color(255, 255, 255);
            }
            if (pie.Slices.Count >= 2)
            {
                pie.Slices[0].FillColor = new ScottPlot.Color(176, 122, 114);
                pie.Slices[1].FillColor = new ScottPlot.Color(41, 106, 160);
            }
            plot.Title($"Уровень ({s.Total})", 10);
            plot.Axes.AutoScale();
            _plotLevel.Refresh();
        }

        private void UpdateHost(DashboardStats s)
        {
            var plot = _plotHost.Plot;
            plot.Clear();
            StylePlot(plot);
            _hostKeys = s.ByHost.Select(x => x.Key).ToList();
            if (_hostKeys.Count == 0)
            {
                plot.Title("Хосты — нет данных", 10);
                _plotHost.Refresh();
                return;
            }
            double[] values = s.ByHost.Select(x => (double)x.Count).ToArray();
            double[] positions = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
            string[] labels = s.ByHost.Select(x => x.Key.Length > 12 ? x.Key.Substring(0, 12) + "…" : x.Key).ToArray();
            var bars = plot.Add.Bars(values);
            bars.Horizontal = true;
            foreach (var bar in bars.Bars)
            {
                bar.Size = 0.6;
                bar.FillColor = new ScottPlot.Color(41, 106, 160); // Host blue #296AA0
                bar.LineColor = new ScottPlot.Color(30, 80, 120);
                bar.Label = bar.Value.ToString();
            }
            bars.ValueLabelStyle.FontSize = 9;
            bars.ValueLabelStyle.Bold = true;
            plot.Axes.Left.SetTicks(positions, labels);
            plot.Axes.Left.TickLabelStyle.FontSize = 8;
            plot.Axes.Margins(left: 0, right: 0.15);
            plot.Title($"Хосты топ-{values.Length}", 10);
            plot.Axes.AutoScale();
            _plotHost.Refresh();
        }

        private void UpdateSource(DashboardStats s)
        {
            var plot = _plotSource.Plot;
            plot.Clear();
            StylePlot(plot);
            _sourceKeys = s.BySource.Select(x => x.Key).ToList();
            if (_sourceKeys.Count == 0)
            {
                plot.Title("Источники — нет данных", 10);
                _plotSource.Refresh();
                return;
            }
            double[] values = s.BySource.Select(x => (double)x.Count).ToArray();
            var bars = plot.Add.Bars(values);
            bars.Horizontal = true;
            foreach (var bar in bars.Bars)
            {
                bar.FillColor = new ScottPlot.Color(0xF5, 0x9E, 0x0B); // Source amber #F59E0B
                bar.LineColor = new ScottPlot.Color(0xB4, 0x53, 0x09);
                bar.Size = 0.6;
                bar.Label = bar.Value.ToString();
            }
            bars.ValueLabelStyle.FontSize = 9;
            bars.ValueLabelStyle.Bold = true;
            double[] pos = Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray();
            string[] labs = s.BySource.Select(x => x.Key.Length > 14 ? x.Key.Substring(0, 14) + "…" : x.Key).ToArray();
            plot.Axes.Left.SetTicks(pos, labs);
            plot.Axes.Left.TickLabelStyle.FontSize = 8;
            plot.Axes.Margins(left: 0, right: 0.15);
            plot.Title($"Источники топ-{values.Length}", 10);
            plot.Axes.AutoScale();
            _plotSource.Refresh();
        }

        private void UpdateTime(DashboardStats s)
        {
            var plot = _plotTime.Plot;
            plot.Clear();
            StylePlot(plot);
            _timeBuckets = s.TimeSeries.Select(x => x.Bucket).ToList();
            if (_timeBuckets.Count == 0)
            {
                plot.Title("Динамика — нет данных", 10);
                _plotTime.Refresh();
                return;
            }
            double[] xs = Enumerable.Range(0, s.TimeSeries.Count).Select(i => (double)i).ToArray();
            double[] ys = s.TimeSeries.Select(x => (double)x.Count).ToArray();
            var scatter = plot.Add.Scatter(xs, ys);
            scatter.LineWidth = 2.5f;
            scatter.Color = new ScottPlot.Color(16, 185, 129); // Time green #10B981
            scatter.MarkerSize = 7;
            scatter.MarkerShape = ScottPlot.MarkerShape.FilledCircle;
            // X labels as time
            string[] xLabs = s.TimeSeries.Select(x => x.Bucket.ToString(s.Period == "30 мин" || s.Period == "1 час" ? "HH:mm" : "dd HH:mm")).ToArray();
            double[] xPos = xs;
            // Only show every Nth label to avoid overlap
            int step = Math.Max(1, xLabs.Length / 4);
            var tickPos = new List<double>();
            var tickLabs = new List<string>();
            for (int i = 0; i < xLabs.Length; i += step)
            {
                tickPos.Add(xPos[i]);
                tickLabs.Add(xLabs[i]);
            }
            plot.Axes.Bottom.SetTicks(tickPos.ToArray(), tickLabs.ToArray());
            plot.Axes.Bottom.TickLabelStyle.Rotation = -20;
            plot.Axes.Bottom.TickLabelStyle.FontSize = 8;
            plot.Title($"Динамика ({s.Period})", 10);
            plot.Axes.AutoScale();
            _plotTime.Refresh();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            try { _plotLevel?.Refresh(); } catch { }
            try { _plotHost?.Refresh(); } catch { }
            try { _plotSource?.Refresh(); } catch { }
            try { _plotTime?.Refresh(); } catch { }
        }
    }
}
