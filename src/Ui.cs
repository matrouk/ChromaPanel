using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace ChromaPanel
{
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(24, 24, 28);
        public static readonly Color Card = Color.FromArgb(32, 32, 39);
        public static readonly Color CardEdge = Color.FromArgb(46, 46, 55);
        public static readonly Color Text = Color.FromArgb(236, 236, 242);
        public static readonly Color Muted = Color.FromArgb(142, 142, 156);
        public static readonly Color Faint = Color.FromArgb(96, 96, 110);
        public static readonly Color Accent = Color.FromArgb(76, 141, 255);
        public static readonly Color Track = Color.FromArgb(52, 52, 62);
        public static readonly Color Ok = Color.FromArgb(74, 222, 128);
        public static readonly Color Warn = Color.FromArgb(251, 191, 36);
        public static readonly Color Err = Color.FromArgb(248, 113, 113);

        public static readonly Color Red = Color.FromArgb(239, 88, 88);
        public static readonly Color Green = Color.FromArgb(74, 200, 105);
        public static readonly Color Blue = Color.FromArgb(84, 132, 245);

        public static Font UI(float size) { return new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font UIBold(float size) { return new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font Mono(float size) { return new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Point); }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || r.Width <= d || r.Height <= d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Rounded panel with an optional heading.</summary>
    internal class Card : Panel
    {
        public string Heading = "";

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Theme.RoundRect(r, 8))
            using (var b = new SolidBrush(Theme.Card))
            using (var pen = new Pen(Theme.CardEdge))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            if (!string.IsNullOrEmpty(Heading))
            {
                using (Font f = Theme.UIBold(9f))
                using (var b = new SolidBrush(Theme.Muted))
                    g.DrawString(Heading.ToUpperInvariant(), f, b, 14, 10);
            }
            base.OnPaint(e);
        }
    }

    /// <summary>
    /// Flat slider. The stock TrackBar cannot be dark-themed and paints a grey
    /// box on any non-system background, so this replaces it.
    /// </summary>
    internal class Slider : Control
    {
        int _min, _max = 100, _value;
        bool _drag;

        public event EventHandler ValueChanged;

        /// <summary>Value that gets a notch drawn on the track (the neutral point).</summary>
        public int Neutral = int.MinValue;

        public Color Fill = Theme.Accent;

        public Slider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Card;
            Height = 26;
            TabStop = true;
        }

        public int Minimum
        {
            get { return _min; }
            set { _min = value; if (_value < _min) Value = _min; Invalidate(); }
        }

        public int Maximum
        {
            get { return _max; }
            set { _max = value; if (_value > _max) Value = _max; Invalidate(); }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value < _min ? _min : (value > _max ? _max : value);
                if (v == _value) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>Sets the value without raising ValueChanged.</summary>
        public void SetQuiet(int value)
        {
            _value = value < _min ? _min : (value > _max ? _max : value);
            Invalidate();
        }

        Rectangle TrackRect
        {
            get { return new Rectangle(8, Height / 2 - 2, Math.Max(1, Width - 16), 4); }
        }

        int ThumbX
        {
            get
            {
                Rectangle t = TrackRect;
                if (_max == _min) return t.X;
                return t.X + (int)Math.Round((double)(_value - _min) / (_max - _min) * t.Width);
            }
        }

        int ValueAt(int x)
        {
            Rectangle t = TrackRect;
            if (t.Width <= 0) return _min;
            double f = (double)(x - t.X) / t.Width;
            if (f < 0) f = 0;
            if (f > 1) f = 1;
            return _min + (int)Math.Round(f * (_max - _min));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            Rectangle t = TrackRect;
            using (var b = new SolidBrush(Theme.Track))
            using (GraphicsPath p = Theme.RoundRect(t, 2))
                g.FillPath(b, p);

            // Neutral notch
            if (Neutral > int.MinValue && _max > _min)
            {
                int nx = t.X + (int)Math.Round((double)(Neutral - _min) / (_max - _min) * t.Width);
                using (var pen = new Pen(Theme.Faint))
                    g.DrawLine(pen, nx, t.Y - 5, nx, t.Bottom + 5);
            }

            int tx = ThumbX;
            int originX = (Neutral > int.MinValue) ? t.X + (int)Math.Round((double)(Neutral - _min) / (_max - _min) * t.Width) : t.X;
            int left = Math.Min(originX, tx), right = Math.Max(originX, tx);
            if (right > left)
            {
                using (var b = new SolidBrush(Fill))
                    g.FillRectangle(b, left, t.Y, right - left, t.Height);
            }

            int r = Focused ? 8 : 7;
            using (var b = new SolidBrush(Fill))
                g.FillEllipse(b, tx - r, Height / 2 - r, r * 2, r * 2);
            using (var b = new SolidBrush(Color.FromArgb(250, 250, 255)))
                g.FillEllipse(b, tx - r + 3, Height / 2 - r + 3, (r - 3) * 2, (r - 3) * 2);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            _drag = true;
            Value = ValueAt(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag) Value = ValueAt(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _drag = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Value = _value + Math.Sign(e.Delta);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int step = Math.Max(1, (_max - _min) / 100);
            int page = Math.Max(1, (_max - _min) / 10);
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: Value = _value - step; e.Handled = true; break;
                case Keys.Right: case Keys.Up: Value = _value + step; e.Handled = true; break;
                case Keys.PageDown: Value = _value - page; e.Handled = true; break;
                case Keys.PageUp: Value = _value + page; e.Handled = true; break;
                case Keys.Home: Value = _min; e.Handled = true; break;
                case Keys.End: Value = _max; e.Handled = true; break;
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    /// <summary>Label + slider + readout, wired together.</summary>
    internal class SliderRow
    {
        public Label Caption;
        public Slider Bar;
        public Label Readout;
        public Label Note;

        /// <summary>Divide the slider's integer value by this for display (gamma uses 100).</summary>
        public double Divisor = 1.0;
        public string Suffix = "";
        public string Format = "0";

        public void Attach(Control parent, int x, int y, int width)
        {
            Caption = new Label();
            Caption.AutoSize = false;
            Caption.Location = new Point(x, y);
            Caption.Size = new Size(112, 18);
            Caption.ForeColor = Theme.Text;
            Caption.Font = Theme.UI(9f);
            Caption.BackColor = Color.Transparent;
            parent.Controls.Add(Caption);

            Readout = new Label();
            Readout.AutoSize = false;
            Readout.Location = new Point(x + width - 64, y);
            Readout.Size = new Size(64, 18);
            Readout.TextAlign = ContentAlignment.MiddleRight;
            Readout.ForeColor = Theme.Muted;
            Readout.Font = Theme.Mono(9f);
            Readout.BackColor = Color.Transparent;
            parent.Controls.Add(Readout);

            Bar = new Slider();
            Bar.Location = new Point(x, y + 20);
            Bar.Size = new Size(width, 26);
            parent.Controls.Add(Bar);
        }

        public double Real { get { return Bar.Value / Divisor; } }

        public void UpdateReadout()
        {
            if (!Bar.Enabled) { Readout.Text = "n/a"; return; }
            Readout.Text = Real.ToString(Format, CultureInfo.InvariantCulture) + Suffix;
        }

        public void SetEnabled(bool on)
        {
            Bar.Enabled = on;
            Bar.Fill = on ? Bar.Fill : Theme.Faint;
            Caption.ForeColor = on ? Theme.Text : Theme.Faint;
            UpdateReadout();
        }
    }

    /// <summary>Flat, themed button.</summary>
    internal class FlatButton : Button
    {
        bool _hover, _down;
        public bool Primary;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = Theme.UI(9f);
            ForeColor = Theme.Text;
            BackColor = Theme.Card;
            Height = 30;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

            Color face = Primary ? Theme.Accent : Color.FromArgb(48, 48, 58);
            if (!Enabled) face = Color.FromArgb(40, 40, 48);
            else if (_down) face = ControlPaint.Dark(face, 0.08f);
            else if (_hover) face = ControlPaint.Light(face, 0.12f);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Theme.RoundRect(r, 6))
            using (var b = new SolidBrush(face))
                g.FillPath(b, p);

            Color fg = Enabled ? (Primary ? Color.White : Theme.Text) : Theme.Faint;
            TextRenderer.DrawText(g, Text, Font, r, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    }

    /// <summary>Dark drop-down. Owner-drawn because the stock control ignores BackColor.</summary>
    internal class DarkCombo : ComboBox
    {
        public DarkCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            BackColor = Color.FromArgb(44, 44, 54);
            ForeColor = Theme.Text;
            Font = Theme.UI(9f);
            ItemHeight = 20;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) { base.OnDrawItem(e); return; }
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Color back = sel ? Theme.Accent : Color.FromArgb(44, 44, 54);
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            string text = Convert.ToString(Items[e.Index], CultureInfo.CurrentCulture);
            TextRenderer.DrawText(e.Graphics, text, Font,
                new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                sel ? Color.White : Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Themed check box.</summary>
    internal class DarkCheck : CheckBox
    {
        bool _hover;

        public DarkCheck()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.UI(9f);
            ForeColor = Theme.Text;
            BackColor = Theme.Card;
            Height = 22;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            var box = new Rectangle(0, Height / 2 - 8, 16, 16);
            using (GraphicsPath p = Theme.RoundRect(box, 4))
            {
                using (var b = new SolidBrush(Checked ? Theme.Accent : Color.FromArgb(44, 44, 54)))
                    g.FillPath(b, p);
                using (var pen = new Pen(Checked ? Theme.Accent : (_hover ? Theme.Muted : Theme.CardEdge)))
                    g.DrawPath(pen, p);
            }
            if (Checked)
            {
                using (var pen = new Pen(Color.White, 2f))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new[]
                    {
                        new Point(box.X + 4, box.Y + 8),
                        new Point(box.X + 7, box.Y + 11),
                        new Point(box.X + 12, box.Y + 5)
                    });
                }
            }
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(box.Right + 8, 0, Width - box.Right - 8, Height),
                Enabled ? ForeColor : Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    }

    /// <summary>Draws the three response curves so the effect is visible at a glance.</summary>
    internal class CurvePreview : Control
    {
        ColourSet _set = new ColourSet();

        public CurvePreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
        }

        public void Show(ColourSet s) { _set = s.Clone(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Theme.RoundRect(r, 6))
            {
                using (var b = new SolidBrush(Color.FromArgb(22, 22, 27))) g.FillPath(b, p);
                using (var pen = new Pen(Theme.CardEdge)) g.DrawPath(pen, p);
            }

            using (var grid = new Pen(Color.FromArgb(38, 38, 46)))
            {
                for (int i = 1; i < 4; i++)
                {
                    int x = r.X + r.Width * i / 4;
                    int y = r.Y + r.Height * i / 4;
                    g.DrawLine(grid, x, r.Y + 1, x, r.Bottom - 1);
                    g.DrawLine(grid, r.X + 1, y, r.Right - 1, y);
                }
            }

            ushort[] ramp = Ramp.Build(_set);
            Color[] cols = { Theme.Red, Theme.Green, Theme.Blue };
            for (int c = 0; c < 3; c++)
            {
                var pts = new PointF[64];
                for (int i = 0; i < 64; i++)
                {
                    int idx = c * 256 + i * 4;
                    float fx = r.X + 1 + (r.Width - 2) * (i / 63f);
                    float fy = r.Bottom - 1 - (r.Height - 2) * (ramp[idx] / 65535f);
                    pts[i] = new PointF(fx, fy);
                }
                using (var pen = new Pen(Color.FromArgb(220, cols[c]), 1.6f))
                    g.DrawLines(pen, pts);
            }
        }
    }

    /// <summary>Small modal text prompt, themed to match.</summary>
    internal static class Prompt
    {
        public static string Ask(IWin32Window owner, string title, string initial)
        {
            using (var dlg = new Form())
            {
                dlg.Text = title;
                dlg.ClientSize = new Size(340, 118);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = Theme.Bg;
                dlg.ForeColor = Theme.Text;
                dlg.Font = Theme.UI(9f);
                dlg.ShowInTaskbar = false;

                var tb = new TextBox();
                tb.Location = new Point(16, 20);
                tb.Size = new Size(308, 24);
                tb.Text = initial ?? "";
                tb.BackColor = Color.FromArgb(44, 44, 54);
                tb.ForeColor = Theme.Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                tb.SelectAll();
                dlg.Controls.Add(tb);

                var ok = new FlatButton();
                ok.Text = "OK";
                ok.Primary = true;
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(158, 66, 78, 30);
                dlg.Controls.Add(ok);

                var cancel = new FlatButton();
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(246, 66, 78, 30);
                dlg.Controls.Add(cancel);

                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                string s = tb.Text.Trim();
                return s.Length == 0 ? null : s;
            }
        }
    }
}
