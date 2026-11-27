using System.Drawing;
using System.Drawing.Drawing2D;

namespace PhoneCamDesktop;

/// <summary>Base for the owner-drawn controls: double-buffered, tracks hover.</summary>
internal abstract class UiControl : Control
{
    protected bool Hover { get; private set; }

    protected UiControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        Font = Ui.Body;
    }

    protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected Graphics Begin(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.HighQuality(g);
        return g;
    }
}

/// <summary>Rounded card that stacks its children vertically and sizes itself to fit them.</summary>
internal sealed class Card : Panel
{
    private readonly string? _title;
    private readonly HashSet<Control> _hidden = new();
    private int Pad => Ui.S(16);
    private int Gap => Ui.S(4);
    private int TitleHeight => _title is null ? 0 : Ui.S(26);

    public Card(string? title)
    {
        _title = title;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Bg;
    }

    /// <summary>
    /// Shows or hides a child. (Control.Visible can't be used for this: it reads false for every
    /// child until the window is on screen.)
    /// </summary>
    public void SetVisible(Control child, bool visible)
    {
        if (visible ? !_hidden.Remove(child) : !_hidden.Add(child)) return;
        child.Visible = visible;
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        int y = Pad + TitleHeight;
        bool any = false;
        foreach (Control c in Controls)
        {
            if (_hidden.Contains(c)) continue;
            c.SetBounds(Pad, y, Math.Max(0, Width - 2 * Pad), c.Height);
            y += c.Height + Gap;
            any = true;
        }
        int height = (any ? y - Gap : y) + Pad - Ui.S(4);
        if (Height != height) Height = height;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.HighQuality(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = Ui.Round(r, Ui.S(16));
        using (var b = new SolidBrush(Ui.Card)) g.FillPath(b, path);
        using (var pen = new Pen(Ui.Border)) g.DrawPath(pen, path);
        if (_title != null)
        {
            TextRenderer.DrawText(g, _title.ToUpperInvariant(), Ui.Section,
                new Rectangle(Pad, Pad - Ui.S(2), Width - 2 * Pad, TitleHeight), Ui.Muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>A label on the left and a field (segmented control, switch, slider…) on the right.</summary>
internal sealed class Row : UiControl
{
    private readonly bool _stretch;
    public Control Field { get; }

    public Row(string label, Control field, bool stretch = true)
    {
        Text = label;
        Field = field;
        _stretch = stretch;
        Height = Ui.S(38);
        Controls.Add(field);
    }

    // Field is null while the base constructor runs (setting Font triggers a layout).
    private int FieldWidth => _stretch ? (int)(Width * 0.6f) : Field?.Width ?? 0;

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (Field is null) return;
        Field.SetBounds(Width - FieldWidth, (Height - Field.Height) / 2, FieldWidth, Field.Height);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Field is ToggleSwitch t) t.Checked = !t.Checked; // clicking the label flips the switch
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        TextRenderer.DrawText(g, Text, Ui.Body, new Rectangle(0, 0, Width - FieldWidth - Ui.S(10), Height), Ui.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

/// <summary>Pill-shaped segmented control; the selected item is white with dark text (like the phone's "on" buttons).</summary>
internal sealed class Segmented : UiControl
{
    private readonly string[] _items;
    private int _selected;
    private int _hot = -1;

    public event EventHandler? SelectedIndexChanged;

    public Segmented(params string[] items)
    {
        _items = items;
        Height = Ui.S(30);
        Font = Ui.Small;
        Cursor = Cursors.Hand;
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            value = Math.Clamp(value, 0, _items.Length - 1);
            if (value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private RectangleF Segment(int i)
    {
        float pad = Ui.S(3);
        float w = (Width - 2 * pad) / _items.Length;
        return new RectangleF(pad + i * w, pad, w, Height - 2 * pad);
    }

    private int IndexAt(Point p)
    {
        for (int i = 0; i < _items.Length; i++)
            if (Segment(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hot = IndexAt(e.Location);
        if (hot != _hot) { _hot = hot; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hot = -1; base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int i = IndexAt(e.Location);
            if (i >= 0) SelectedIndex = i;
        }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        Ui.FillRound(g, new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f, Ui.Track);
        for (int i = 0; i < _items.Length; i++)
        {
            var r = Segment(i);
            Color text = Ui.Muted;
            if (i == _selected)
            {
                Ui.FillRound(g, r, r.Height / 2, Ui.OnFill);
                text = Ui.OnText;
            }
            else if (i == _hot)
            {
                Ui.FillRound(g, r, r.Height / 2, Ui.TrackHover);
                text = Ui.Text;
            }
            TextRenderer.DrawText(g, _items[i], i == _selected ? Ui.SmallBold : Font, Rectangle.Round(r), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>iOS-style on/off switch.</summary>
internal sealed class ToggleSwitch : UiControl
{
    private bool _on;
    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        Size = new Size(Ui.S(42), Ui.S(24));
        Cursor = Cursors.Hand;
    }

    public bool Checked
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) Checked = !Checked;
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        var track = new RectangleF(0, 0, Width - 1, Height - 1);
        Ui.FillRound(g, track, track.Height / 2, _on ? Ui.OnFill : (Hover ? Ui.TrackHover : Ui.Track));
        float inset = Ui.S(3);
        float d = Height - 2 * inset;
        float x = _on ? Width - d - inset - 1 : inset;
        using var knob = new SolidBrush(_on ? Ui.OnText : Color.FromArgb(205, 209, 216));
        g.FillEllipse(knob, x, inset - 0.5f, d, d);
    }
}

/// <summary>Rounded button.</summary>
internal sealed class PillButton : UiControl
{
    public PillButton()
    {
        Height = Ui.S(30);
        Font = Ui.Small;
        Cursor = Cursors.Hand;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        Ui.FillRound(g, r, r.Height / 2, Hover ? Ui.TrackHover : Ui.Track);
        TextRenderer.DrawText(g, Text, Font, Rectangle.Inflate(ClientRectangle, -Ui.S(10), 0), Ui.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

/// <summary>Minimal slider: thin track, white fill and knob.</summary>
internal sealed class Slider : UiControl
{
    private readonly float _min, _max;
    private float _value;
    private bool _dragging;

    public event EventHandler? ValueChanged;

    public Slider(float min, float max)
    {
        _min = min;
        _max = max;
        _value = min;
        Height = Ui.S(24);
        Cursor = Cursors.Hand;
    }

    public float Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(value, _min, _max);
            if (Math.Abs(value - _value) < 0.0005f) return;
            _value = value;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private float KnobRadius => Ui.S(8);
    private float Left0 => KnobRadius + 1;
    private float Right0 => Width - KnobRadius - 1;

    private void SetFromX(int x)
    {
        float t = Math.Clamp((x - Left0) / Math.Max(1, Right0 - Left0), 0, 1);
        Value = _min + t * (_max - _min);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            Capture = true;
            SetFromX(e.X);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) SetFromX(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        float cy = Height / 2f;
        float th = Ui.S(4);
        float t = (_max - _min) <= 0 ? 0 : (_value - _min) / (_max - _min);
        float kx = Left0 + t * (Right0 - Left0);
        Ui.FillRound(g, new RectangleF(Left0, cy - th / 2, Right0 - Left0, th), th / 2, Ui.Track);
        Ui.FillRound(g, new RectangleF(Left0, cy - th / 2, kx - Left0, th), th / 2, Ui.OnFill);
        float r = KnobRadius * (Hover || _dragging ? 1f : 0.85f);
        using var knob = new SolidBrush(Ui.OnFill);
        g.FillEllipse(knob, kx - r, cy - r, 2 * r, 2 * r);
    }
}

/// <summary>Thin audio level bar.</summary>
internal sealed class LevelMeter : UiControl
{
    private float _level;

    public LevelMeter()
    {
        Height = Ui.S(8);
    }

    /// <summary>0–1 (peak). Shown on a speech-friendly curve so normal talking fills about half.</summary>
    public float Level
    {
        get => _level;
        set
        {
            value = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(value - _level) < 0.005f) return;
            _level = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        var track = new RectangleF(0, 0, Width - 1, Height - 1);
        Ui.FillRound(g, track, track.Height / 2, Ui.Track);
        float shown = (float)Math.Sqrt(_level);
        if (shown <= 0.01f) return;
        var fill = new RectangleF(0, 0, Math.Max(track.Height, track.Width * shown), track.Height);
        Ui.FillRound(g, fill, fill.Height / 2, _level > 0.9f ? Ui.Live : Ui.Ok);
    }
}

/// <summary>Dark styling for drop-down menus.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Ui.Text : Ui.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        Ui.HighQuality(g);
        var r = e.ImageRectangle;
        float d = Ui.S(7);
        using var b = new SolidBrush(Ui.Text);
        g.FillEllipse(b, r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Ui.Card;
        public override Color ImageMarginGradientBegin => Ui.Card;
        public override Color ImageMarginGradientMiddle => Ui.Card;
        public override Color ImageMarginGradientEnd => Ui.Card;
        public override Color MenuBorder => Ui.Border;
        public override Color MenuItemBorder => Ui.TrackHover;
        public override Color MenuItemSelected => Ui.TrackHover;
        public override Color MenuItemSelectedGradientBegin => Ui.TrackHover;
        public override Color MenuItemSelectedGradientEnd => Ui.TrackHover;
        public override Color SeparatorDark => Ui.Border;
        public override Color SeparatorLight => Ui.Border;
        public override Color CheckBackground => Ui.Card;
        public override Color CheckSelectedBackground => Ui.TrackHover;
        public override Color CheckPressedBackground => Ui.TrackHover;
    }
}

/// <summary>Wrapped, muted text that sizes its height to fit.</summary>
internal sealed class Note : UiControl
{
    private Color _color = Ui.Muted;
    private int _measuredWidth = -1;

    public Note()
    {
        Font = Ui.Small;
        Height = Ui.S(18);
    }

    public Color TextColor
    {
        get => _color;
        set { _color = value; Invalidate(); }
    }

    private const TextFormatFlags Flags =
        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl | TextFormatFlags.Left;

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        _measuredWidth = -1;
        Remeasure();
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Remeasure();
    }

    private void Remeasure()
    {
        if (Width <= 0 || Width == _measuredWidth) return;
        _measuredWidth = Width;
        var size = TextRenderer.MeasureText(Text, Font, new Size(Width, int.MaxValue), Flags);
        int h = Math.Max(Ui.S(16), size.Height + Ui.S(4));
        if (h != Height) Height = h;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, _color, Flags);
    }
}

/// <summary>App icon, name and a connection dot.</summary>
internal sealed class Header : UiControl
{
    private string _status = "No iPhone connected";
    private bool _ok;
    private Icon? _icon;

    public Header()
    {
        BackColor = Ui.Bg;
        Height = Ui.S(56);
    }

    public void SetStatus(string status, bool ok)
    {
        if (status == _status && ok == _ok) return;
        _status = status;
        _ok = ok;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        int size = Ui.S(44);
        if (Ui.AppIcon is { } appIcon)
        {
            _icon ??= new Icon(appIcon, size, size);
            g.DrawIcon(_icon, new Rectangle(0, (Height - size) / 2, size, size));
        }
        int x = size + Ui.S(12);
        TextRenderer.DrawText(g, "PhoneCam", Ui.Title, new Point(x, Ui.S(5)), Ui.Text, TextFormatFlags.NoPadding);
        float dot = Ui.S(7);
        float dy = Ui.S(37);
        using (var b = new SolidBrush(_ok ? Ui.Ok : Ui.Muted)) g.FillEllipse(b, x, dy - dot / 2, dot, dot);
        TextRenderer.DrawText(g, _status, Ui.Small, new Point(x + Ui.S(13), (int)dy - Ui.S(8)), Ui.Muted,
            TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The large webcam preview with a LIVE badge and a message card, as on the phone.</summary>
internal sealed class PreviewView : UiControl
{
    private Bitmap? _image;
    private bool _live;
    private string _badge = "OFFLINE";
    private string? _title;
    private string? _detail;

    public PreviewView()
    {
        BackColor = Ui.Bg;
    }

    /// <summary>The caller owns the bitmap and disposes the previous one.</summary>
    public Bitmap? Image
    {
        get => _image;
        set { _image = value; Invalidate(); }
    }

    public void SetOverlay(bool live, string badge, string? title, string? detail)
    {
        if (live == _live && badge == _badge && title == _title && detail == _detail) return;
        (_live, _badge, _title, _detail) = (live, badge, title, detail);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        var area = new Rectangle(Ui.S(20), Ui.S(20), Width - Ui.S(32), Height - Ui.S(40));
        if (area.Width < 10 || area.Height < 10) return;

        float aspect = _image != null ? (float)_image.Width / _image.Height : 16f / 9f;
        float w = area.Width, h = w / aspect;
        if (h > area.Height) { h = area.Height; w = h * aspect; }
        var frame = new RectangleF(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);

        using var path = Ui.Round(frame, Ui.S(18));
        if (_image != null)
        {
            // A texture brush gives anti-aliased rounded corners (SetClip would be jagged).
            using var tb = new TextureBrush(_image, WrapMode.Clamp);
            tb.TranslateTransform(frame.X, frame.Y);
            tb.ScaleTransform(frame.Width / _image.Width, frame.Height / _image.Height);
            g.FillPath(tb, path);
        }
        else
        {
            using var b = new SolidBrush(Ui.Card);
            g.FillPath(b, path);
        }
        using (var pen = new Pen(Ui.Border)) g.DrawPath(pen, path);

        DrawBadge(g, new PointF(frame.X + Ui.S(16), frame.Y + Ui.S(16)));
        if (_title != null) DrawMessage(g, frame);
    }

    private void DrawBadge(Graphics g, PointF at)
    {
        var textSize = TextRenderer.MeasureText(_badge, Ui.SmallBold, Size.Empty, TextFormatFlags.NoPadding);
        float pad = Ui.S(11), dot = Ui.S(8), height = Ui.S(28);
        var r = new RectangleF(at.X, at.Y, pad + dot + Ui.S(8) + textSize.Width + pad, height);
        Ui.FillRound(g, r, height / 2, Color.FromArgb(170, 12, 13, 16));
        var dotRect = new RectangleF(r.X + pad, r.Y + (height - dot) / 2, dot, dot);
        if (_live)
        {
            using var glow = new SolidBrush(Color.FromArgb(90, Ui.Live));
            var gr = dotRect;
            gr.Inflate(Ui.S(3), Ui.S(3));
            g.FillEllipse(glow, gr);
        }
        using (var b = new SolidBrush(_live ? Ui.Live : Color.FromArgb(120, 255, 255, 255))) g.FillEllipse(b, dotRect);
        TextRenderer.DrawText(g, _badge, Ui.SmallBold,
            new Rectangle((int)(dotRect.Right + Ui.S(8)), (int)r.Y, textSize.Width + Ui.S(4), (int)height), Ui.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    private void DrawMessage(Graphics g, RectangleF frame)
    {
        int pad = Ui.S(22);
        int width = Math.Min(Ui.S(400), (int)frame.Width - Ui.S(40));
        if (width < Ui.S(120)) return;
        const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter |
                                     TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;
        var titleSize = TextRenderer.MeasureText(_title, Ui.Heading, new Size(width - 2 * pad, int.MaxValue), wrap);
        var detailSize = string.IsNullOrEmpty(_detail)
            ? Size.Empty
            : TextRenderer.MeasureText(_detail, Ui.Body, new Size(width - 2 * pad, int.MaxValue), wrap);
        int height = pad + titleSize.Height + (detailSize.Height > 0 ? Ui.S(8) + detailSize.Height : 0) + pad;

        var card = new Rectangle((int)(frame.X + (frame.Width - width) / 2), (int)(frame.Y + (frame.Height - height) / 2), width, height);
        Ui.FillRound(g, card, Ui.S(20), Color.FromArgb(235, 23, 25, 30));
        using (var path = Ui.Round(card, Ui.S(20)))
        using (var pen = new Pen(Ui.Border))
            g.DrawPath(pen, path);

        var titleRect = new Rectangle(card.X + pad, card.Y + pad, width - 2 * pad, titleSize.Height);
        TextRenderer.DrawText(g, _title, Ui.Heading, titleRect, Ui.Text, wrap);
        if (detailSize.Height > 0)
        {
            var detailRect = new Rectangle(card.X + pad, titleRect.Bottom + Ui.S(8), width - 2 * pad, detailSize.Height);
            TextRenderer.DrawText(g, _detail, Ui.Body, detailRect, Ui.Muted, wrap);
        }
    }
}
