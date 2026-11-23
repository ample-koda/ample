namespace Ample.Player;

/// <summary>The colours of the player (the Visual Studio dark theme, with the AMPLE orange as the accent).</summary>
internal static class Theme
{
    public static readonly Color Back = Color.FromArgb(30, 30, 30);
    public static readonly Color Panel = Color.FromArgb(37, 37, 38);
    public static readonly Color Raised = Color.FromArgb(51, 51, 55);
    public static readonly Color Hover = Color.FromArgb(70, 70, 76);
    public static readonly Color Text = Color.FromArgb(220, 220, 220);
    public static readonly Color Dim = Color.FromArgb(150, 150, 150);
    public static readonly Color Accent = Color.FromArgb(255, 192, 140);
    public static readonly Color AccentDim = Color.FromArgb(120, 90, 65);
}

/// <summary>A flat round-cornered button that shows an icon-font glyph or a text, and can be switched on (the loop button).</summary>
internal sealed class IconButton : Control
{
    private bool _hover, _down;

    public IconButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    /// <summary>The glyph (an icon-font character) to draw.</summary>
    public string Glyph { get => Text; set { Text = value; Invalidate(); } }

    /// <summary>Whether it is a toggle that is switched on (drawn in the accent colour).</summary>
    public bool On { get; set; }

    /// <summary>The icon font size in points.</summary>
    public float GlyphSize { get; set; } = 15f;

    /// <summary>Draw the text in the normal font instead of the icon font (for a word on the button).</summary>
    public bool PlainText { get; set; }

    protected override void OnMouseEnter(System.EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(System.EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Back);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Color fill = On ? Theme.AccentDim : _down ? Theme.Hover : _hover ? Theme.Raised : Theme.Panel;
        using System.Drawing.Drawing2D.GraphicsPath path = RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), (int)(Height * 0.22));
        using SolidBrush brush = new(fill);
        e.Graphics.FillPath(brush, path);
        using Pen border = new(On ? Theme.Accent : Theme.Hover);
        e.Graphics.DrawPath(border, path);
        using Font font = new(PlainText ? "Segoe UI Semibold" : "Segoe MDL2 Assets", GlyphSize);
        using SolidBrush ink = new(On ? Theme.Accent : Theme.Text);
        using StringFormat centre = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        e.Graphics.DrawString(Text, font, ink, new RectangleF(0, 0, Width, Height), centre);
    }

    /// <summary>A rectangle with rounded corners.</summary>
    public static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(Rectangle r, int radius)
    {
        System.Drawing.Drawing2D.GraphicsPath path = new();
        int d = System.Math.Max(2, radius * 2);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>A thin horizontal slider (the volume): drag or click, a value from 0 to 1.</summary>
internal sealed class Slider : Control
{
    private double _value = 0.8;
    private bool _dragging;

    public Slider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    /// <summary>Raised while the value changes by the user.</summary>
    public event System.EventHandler? Changed;

    /// <summary>Raised when the user lets go.</summary>
    public event System.EventHandler? Released;

    /// <summary>The value, 0..1.</summary>
    public double Value
    {
        get => _value;
        set { _value = System.Math.Max(0, System.Math.Min(1, value)); Invalidate(); }
    }

    private void Set(int x)
    {
        int pad = Height / 2;
        Value = (double)(x - pad) / System.Math.Max(1, Width - 2 * pad);
        Changed?.Invoke(this, System.EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e) { _dragging = true; Set(e.X); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_dragging) Set(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; Released?.Invoke(this, System.EventArgs.Empty); base.OnMouseUp(e); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Value += e.Delta > 0 ? 0.05 : -0.05;
        Changed?.Invoke(this, System.EventArgs.Empty);
        Released?.Invoke(this, System.EventArgs.Empty);
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Back);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int pad = Height / 2, mid = Height / 2, thick = System.Math.Max(4, Height / 5);
        int x = pad + (int)((Width - 2 * pad) * _value);
        using SolidBrush track = new(Theme.Raised), done = new(Theme.Accent), knob = new(Theme.Text);
        e.Graphics.FillRectangle(track, pad, mid - thick / 2, Width - 2 * pad, thick);
        e.Graphics.FillRectangle(done, pad, mid - thick / 2, x - pad, thick);
        e.Graphics.FillEllipse(knob, x - pad + 2, 2, Height - 5, Height - 5);
    }
}

/// <summary>
/// The waveform of the song with the play position and the loop region. A click moves the position, a drag selects a loop region, a double click
/// removes the region, the mouse wheel scrubs.
/// </summary>
internal sealed class WaveView : Control
{
    private readonly WavData _wav;
    private (float[][] Min, float[][] Max, float[][] Rms) _envelope = ([], [], []);
    private int _envelopeWidth;
    private int _dragFrom = -1, _dragTo;
    private bool _dragged;

    public WaveView(WavData wav)
    {
        _wav = wav;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.IBeam;
    }

    /// <summary>The play position, in frames.</summary>
    public long Position { get; set; }

    /// <summary>The loop region in frames (an end after the start means there is one).</summary>
    public long RegionStart { get; set; }

    /// <summary>The end of the loop region in frames.</summary>
    public long RegionEnd { get; set; }

    /// <summary>Whether a stereo file is drawn as two lanes (left above right) instead of both channels together.</summary>
    public bool Split { get; set; } = true;

    /// <summary>Whether the loop is on (the region is drawn brighter).</summary>
    public bool LoopOn { get; set; }

    /// <summary>The user clicked: a position in frames.</summary>
    public event System.Action<long>? Seek;

    /// <summary>The user selected a region (start, end in frames).</summary>
    public event System.Action<long, long>? RegionPicked;

    /// <summary>The user double clicked: remove the region.</summary>
    public event System.Action? ClearRegion;

    /// <summary>The mouse wheel: seconds to move (negative = back).</summary>
    public event System.Action<double>? Scrub;

    private long FrameAt(int x) => (long)(System.Math.Max(0, System.Math.Min(Width - 1, x)) / (double)System.Math.Max(1, Width) * _wav.Frames);
    private int XAt(long frame) => (int)(frame / (double)System.Math.Max(1, _wav.Frames) * Width);

    protected override void OnMouseDown(MouseEventArgs e) { _dragFrom = e.X; _dragTo = e.X; _dragged = false; base.OnMouseDown(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom >= 0 && e.Button == MouseButtons.Left)
        {
            _dragTo = e.X;
            if (System.Math.Abs(_dragTo - _dragFrom) > 5) _dragged = true;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragFrom >= 0)
        {
            if (_dragged)
            {
                long a = FrameAt(System.Math.Min(_dragFrom, _dragTo)), b = FrameAt(System.Math.Max(_dragFrom, _dragTo));
                if (b - a > _wav.Rate / 10) RegionPicked?.Invoke(a, b);
            }
            else
            {
                Seek?.Invoke(FrameAt(e.X));
            }
        }

        _dragFrom = -1;
        _dragged = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e) { ClearRegion?.Invoke(); base.OnMouseDoubleClick(e); }

    protected override void OnMouseWheel(MouseEventArgs e) { Scrub?.Invoke(e.Delta > 0 ? 2 : -2); base.OnMouseWheel(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Panel);
        if (_envelopeWidth != Width) { _envelope = _wav.Envelope(System.Math.Max(1, Width)); _envelopeWidth = Width; }
        float k = DeviceDpi / 96f;
        int ruler = (int)(18 * k);
        bool hasRegion = RegionEnd > RegionStart;
        if (hasRegion)
        {
            using SolidBrush shade = new(Color.FromArgb(LoopOn ? 60 : 30, Theme.Accent));
            g.FillRectangle(shade, XAt(RegionStart), 0, System.Math.Max(2, XAt(RegionEnd) - XAt(RegionStart)), Height);
        }

        if (_dragFrom >= 0 && _dragged)
        {
            using SolidBrush pick = new(Color.FromArgb(70, Theme.Text));
            g.FillRectangle(pick, System.Math.Min(_dragFrom, _dragTo), 0, System.Math.Abs(_dragTo - _dragFrom), Height);
        }

        DrawRuler(g, ruler, k);
        int playX = XAt(Position);
        bool split = Split && _wav.Channels == 2;
        int lanes = split ? 2 : 1, laneHeight = (Height - ruler) / lanes;
        for (int lane = 0; lane < lanes; lane++) DrawLane(g, lane, ruler + lane * laneHeight, laneHeight, playX, split, k);
        using Pen head = new(Color.White, 2);
        g.DrawLine(head, playX, 0, playX, Height);
        if (hasRegion)
        {
            using Pen edge = new(Theme.Accent, 2);
            g.DrawLine(edge, XAt(RegionStart), 0, XAt(RegionStart), Height);
            g.DrawLine(edge, XAt(RegionEnd), 0, XAt(RegionEnd), Height);
        }
    }

    /// <summary>Time marks along the top, a step of 1, 2, 5, 10, 15, 30 s ... that keeps them about 80 px apart.</summary>
    private void DrawRuler(Graphics g, int ruler, float k)
    {
        double seconds = _wav.Seconds;
        if (seconds <= 0) return;
        double[] steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];
        double step = steps[steps.Length - 1];
        foreach (double s in steps)
        {
            if (s / seconds * Width >= 80 * k) { step = s; break; }
        }

        using Pen tick = new(Color.FromArgb(90, 90, 96));
        using SolidBrush ink = new(Theme.Dim);
        using Font small = new("Segoe UI", 7.5f);
        for (double t = 0; t < seconds; t += step)
        {
            int x = (int)(t / seconds * Width);
            g.DrawLine(tick, x, ruler - (int)(6 * k), x, ruler);
            int minutes = (int)(t / 60);
            g.DrawString(minutes + ":" + ((int)t % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture), small, ink, x + 2, 0);
        }
    }

    /// <summary>
    /// One lane: the outer shape is the lowest to highest sample of each pixel column, the brighter core is the RMS (the average loudness).
    /// Together, a lane shows both channels as one (the extremes of both, the RMS of both).
    /// </summary>
    private void DrawLane(Graphics g, int lane, int top, int height, int playX, bool split, float k)
    {
        int mid = top + height / 2, half = height / 2 - (int)(3 * k);
        using Pen centre = new(Color.FromArgb(70, 70, 76)), quarter = new(Color.FromArgb(45, 45, 50));
        g.DrawLine(quarter, 0, mid - half / 2, Width, mid - half / 2);
        g.DrawLine(quarter, 0, mid + half / 2, Width, mid + half / 2);
        g.DrawLine(centre, 0, mid, Width, mid);
        using Pen outerPlayed = new(Color.FromArgb(200, 150, 110)), outerRest = new(Color.FromArgb(100, 100, 108));
        using Pen corePlayed = new(Theme.Accent), coreRest = new(Color.FromArgb(165, 165, 175));
        int columns = _envelope.Min.Length == 0 ? 0 : _envelope.Min[0].Length;
        for (int x = 0; x < columns; x++)
        {
            float lo = 0, hi = 0, power = 0;
            for (int ch = 0; ch < _wav.Channels; ch++)
            {
                if (split && ch != lane) continue;
                lo = System.Math.Min(lo, _envelope.Min[ch][x]);
                hi = System.Math.Max(hi, _envelope.Max[ch][x]);
                power += _envelope.Rms[ch][x] * _envelope.Rms[ch][x];
            }

            float rms = (float)System.Math.Sqrt(power / (split ? 1 : _wav.Channels));
            bool done = x < playX;
            g.DrawLine(done ? outerPlayed : outerRest, x, mid - System.Math.Max(1, (int)(hi * half)), x, mid + System.Math.Max(1, (int)(-lo * half)));
            int core = (int)(System.Math.Min(1f, rms) * half);
            if (core > 0) g.DrawLine(done ? corePlayed : coreRest, x, mid - core, x, mid + core);
        }

        if (!split) return;
        using SolidBrush ink = new(Theme.Dim);
        using Font small = new("Segoe UI Semibold", 8f);
        g.DrawString(lane == 0 ? "L" : "R", small, ink, 4, top + 1);
    }
}
