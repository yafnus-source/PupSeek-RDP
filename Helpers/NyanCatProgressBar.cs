using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ShadowRDP.Helpers
{
    public class NyanCatProgressBar : Control
    {
        private Timer _timer = new Timer();
        private int _frame;
        private int _starTick;
        private Random _rng = new Random();
        private float _progress = 0f; // 0..1, left to right
        private PointF[] _stars = new PointF[14];
        private bool _fading;
        private int _fadeAlpha = 255;
        private Stopwatch _sw = new Stopwatch();

        private static readonly Color[] Rainbow = {
            Color.FromArgb(0xFF, 0x00, 0x00),
            Color.FromArgb(0xFF, 0x99, 0x00),
            Color.FromArgb(0xFF, 0xFF, 0x00),
            Color.FromArgb(0x00, 0xCC, 0x00),
            Color.FromArgb(0x00, 0x99, 0xFF),
            Color.FromArgb(0x99, 0x33, 0xFF)
        };

        public NyanCatProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            Height = 36;
            BackColor = Color.Transparent;
            for (int i = 0; i < _stars.Length; i++)
                _stars[i] = new PointF(_rng.Next(0, 500), _rng.Next(2, Height - 4));

            _timer.Interval = 50;
            _timer.Tick += (s, e) =>
            {
                _frame = (_frame + 1) % 4;
                _starTick++;

                if (_fading)
                {
                    _fadeAlpha -= 15;
                    if (_fadeAlpha <= 0) { Stop(); return; }
                }
                else if (_sw.IsRunning)
                {
                    // Progress based on real elapsed time: approaches 1 asymptotically
                    // At 10s -> 0.50, 20s -> 0.74, 30s -> 0.86, 60s -> 0.98
                    double elapsed = _sw.Elapsed.TotalSeconds;
                    _progress = Math.Min(1f, (float)(1.0 - Math.Exp(-elapsed / 12.0)));
                }

                // Move stars left
                for (int i = 0; i < _stars.Length; i++)
                {
                    _stars[i].X -= 3f;
                    if (_stars[i].X < -10)
                    {
                        _stars[i].X = Width + _rng.Next(10, 80);
                        _stars[i].Y = _rng.Next(2, Height - 4);
                    }
                }
                Invalidate();
            };
        }

        public void Start()
        {
            _progress = 0f;
            _fading = false;
            _fadeAlpha = 255;
            _sw.Restart();
            Visible = true;
            if (!_timer.Enabled) _timer.Start();
            Invalidate();
        }

        public void Stop()
        {
            _timer.Stop();
            _sw.Stop();
            Visible = false;
            _fading = false;
        }

        public void FadeOut()
        {
            _fading = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;

            int h = Height;
            int w = Width;
            int catW = 20;
            int catH = 14;
            int catY = (h - catH) / 2;

            int alpha = _fadeAlpha;
            if (alpha <= 0) return;

            // Stars
            using var starBrush = new SolidBrush(Color.FromArgb(Math.Max(0, alpha * 140 / 255), 255, 255, 255));
            for (int i = 0; i < _stars.Length; i++)
            {
                float sx = _stars[i].X;
                float sy = _stars[i].Y;
                if (sx < 0 || sx > w) continue;
                if (((_starTick + i * 3) % 8) < 6)
                {
                    int sz = ((_starTick + i * 3) % 8 == 0) ? 3 : 2;
                    g.FillRectangle(starBrush, sx, sy, sz, sz);
                }
            }

            // Cat position: smooth left to right based on real time
            int catX = (int)(_progress * (w - catW));

            // Rainbow trail: from 0 to catX
            int trailEnd = catX + 2;
            int stripeH = 2;
            for (int i = 0; i < Rainbow.Length; i++)
            {
                int sy = catY + 1 + i * stripeH;
                var c = Rainbow[i];
                using var brush = new SolidBrush(Color.FromArgb(Math.Max(0, alpha * c.A / 255), c.R, c.G, c.B));
                g.FillRectangle(brush, 0, sy, Math.Max(0, trailEnd), stripeH);
            }

            // Nyan Cat body (pop-tart)
            using var bodyBrush = new SolidBrush(Color.FromArgb(alpha, 0xD4, 0xA5, 0x6A));
            using var bodyEdge = new Pen(Color.FromArgb(alpha, 0xC0, 0x80, 0x40), 1f);
            var bodyRect = new Rectangle(catX + 4, catY + 1, catW - 8, catH - 2);
            g.FillRectangle(bodyBrush, bodyRect);
            g.DrawRectangle(bodyEdge, bodyRect);

            // Sprinkles
            Color[] sprinkleColors = { Color.FromArgb(0xFF, 0x66, 0x99), Color.FromArgb(0x66, 0xCC, 0xFF), Color.FromArgb(0xFF, 0xFF, 0x66), Color.FromArgb(0x99, 0xFF, 0x66) };
            for (int i = 0; i < 4; i++)
            {
                using var sb = new SolidBrush(Color.FromArgb(alpha, sprinkleColors[i].R, sprinkleColors[i].G, sprinkleColors[i].B));
                g.FillRectangle(sb, catX + 6 + i * 3, catY + 3 + (i % 2) * 4, 1, 1);
            }

            // Cat head
            using var headBrush = new SolidBrush(Color.FromArgb(alpha, 0x80, 0x80, 0x80));
            g.FillEllipse(headBrush, catX + catW - 8, catY, 7, 7);

            // Ears
            using var earBrush = new SolidBrush(Color.FromArgb(alpha, 0x80, 0x80, 0x80));
            g.FillPolygon(earBrush, new[] { new Point(catX + catW - 8, catY + 1), new Point(catX + catW - 6, catY - 2), new Point(catX + catW - 5, catY + 1) });
            g.FillPolygon(earBrush, new[] { new Point(catX + catW - 5, catY + 1), new Point(catX + catW - 3, catY - 2), new Point(catX + catW - 2, catY + 1) });

            // Eyes
            using var eyeBrush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
            g.FillRectangle(eyeBrush, catX + catW - 7, catY + 3, 1, 1);
            g.FillRectangle(eyeBrush, catX + catW - 5, catY + 3, 1, 1);

            // Tail (animated)
            using var tailPen = new Pen(Color.FromArgb(alpha, 0x80, 0x80, 0x80), 1.2f);
            int tailX = catX + 2;
            int tailY = catY + 6;
            int w1 = (_frame % 2 == 0) ? -1 : 1;
            g.DrawLines(tailPen, new[] {
                new Point(tailX, tailY),
                new Point(tailX - 2, tailY - 2 + w1),
                new Point(tailX - 4, tailY + w1),
                new Point(tailX - 6, tailY - 1 + w1)
            });

            // Legs
            using var legBrush = new SolidBrush(Color.FromArgb(alpha, 0x80, 0x80, 0x80));
            g.FillRectangle(legBrush, catX + 6, catY + catH - 2, 1, 2);
            g.FillRectangle(legBrush, catX + 10, catY + catH - 2, 1, 2);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer?.Stop(); _timer?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
