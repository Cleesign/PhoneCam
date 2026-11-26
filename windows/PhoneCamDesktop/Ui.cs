using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PhoneCamDesktop;

/// <summary>Colours, fonts and drawing helpers shared by the custom controls (dark, minimal — like the iPhone app).</summary>
internal static class Ui
{
    /// <summary>Screen DPI scale (1.0 = 100 %). Set once at start-up.</summary>
    public static float Scale = 1f;
    public static int S(float v) => (int)Math.Round(v * Scale);

    public static readonly Color Bg = Color.FromArgb(11, 12, 15);
    public static readonly Color Card = Color.FromArgb(23, 25, 30);
    public static readonly Color Track = Color.FromArgb(37, 40, 47);
    public static readonly Color TrackHover = Color.FromArgb(50, 54, 63);
    public static readonly Color Border = Color.FromArgb(36, 39, 46);
    public static readonly Color Text = Color.FromArgb(242, 243, 245);
    public static readonly Color Muted = Color.FromArgb(148, 154, 166);
    public static readonly Color Live = Color.FromArgb(235, 60, 60);
    public static readonly Color Ok = Color.FromArgb(52, 199, 89);
    public static readonly Color Error = Color.FromArgb(255, 105, 97);
    public static readonly Color OnFill = Color.White;
    public static readonly Color OnText = Color.FromArgb(11, 12, 15);

    private static readonly string TextFamily = Pick("Segoe UI Variable Text", "Segoe UI");
    private static readonly string DisplayFamily = Pick("Segoe UI Variable Display", "Segoe UI");

    public static readonly Font Body = new(TextFamily, 9.5f);
    public static readonly Font Small = new(TextFamily, 8.75f);
    public static readonly Font SmallBold = new(TextFamily, 8.25f, FontStyle.Bold);
    public static readonly Font Section = new(TextFamily, 7.75f, FontStyle.Bold);
    public static readonly Font Heading = new(DisplayFamily, 12f, FontStyle.Bold);
    public static readonly Font Title = new(DisplayFamily, 15f, FontStyle.Bold);

    private static readonly Lazy<Icon?> _appIcon = new(() =>
    {
        using var s = typeof(Ui).Assembly.GetManifestResourceStream("PhoneCamDesktop.app.ico");
        return s is null ? null : new Icon(s);
    });
    public static Icon? AppIcon => _appIcon.Value;

    private static string Pick(params string[] names)
    {
        using var installed = new InstalledFontCollection();
        var set = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.FirstOrDefault(set.Contains) ?? "Segoe UI";
    }

    public static void HighQuality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, RectangleF r, float radius, Color color)
    {
        using var path = Round(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }
}

internal static class NativeUi
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Dark title bar (Windows 10 20H1+) in the app's background colour (Windows 11).</summary>
    public static void DarkTitleBar(IntPtr hwnd, Color caption)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));           // DWMWA_USE_IMMERSIVE_DARK_MODE
            int colorRef = caption.R | (caption.G << 8) | (caption.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref colorRef, sizeof(int));     // DWMWA_CAPTION_COLOR
        }
        catch (Exception) { /* older Windows: keep the default title bar */ }
    }

    public static void DarkScrollbars(IntPtr hwnd)
    {
        try { SetWindowTheme(hwnd, "DarkMode_Explorer", null); }
        catch (Exception) { /* ignore */ }
    }
}
