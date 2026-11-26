using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace PhoneCamDesktop;

internal enum WatermarkPosition { TopLeft, TopRight, BottomLeft, BottomRight, Center }

/// <summary>
/// Decodes JPEG frames from the phone and draws them — rotated, mirrored, cropped or letterboxed —
/// into a fixed-size 24-bit BGR bitmap that is handed to the virtual camera.
/// </summary>
internal sealed class FrameRenderer : IDisposable
{
    private readonly Bitmap _target;
    private readonly Graphics _g;
    private readonly ImageAttributes _edgeClamp = new();

    // Watermark: the source image (owned by the caller) and a pre-scaled copy with opacity baked in.
    private Bitmap? _wmSource;
    private Bitmap? _wmCache;
    private WatermarkPosition _wmPosition = WatermarkPosition.BottomRight;
    private float _wmSize = 0.18f;
    private float _wmOpacity = 0.9f;

    public int Width { get; }
    public int Height { get; }

    public FrameRenderer(int width, int height)
    {
        Width = width;
        Height = height;
        _target = new Bitmap(width, height, PixelFormat.Format24bppRgb); // GDI+ "24bppRgb" is BGR in memory
        _g = Graphics.FromImage(_target);
        _g.InterpolationMode = InterpolationMode.Bilinear;
        _g.PixelOffsetMode = PixelOffsetMode.Half;
        _g.CompositingMode = CompositingMode.SourceCopy;
        _g.SmoothingMode = SmoothingMode.None;
        _edgeClamp.SetWrapMode(WrapMode.TileFlipXY); // avoids dark seams at the edges when scaling
    }

    /// <param name="rotation">0, 90, 180 or 270 degrees clockwise.</param>
    /// <param name="mirror">Flip horizontally (after rotation).</param>
    /// <param name="fill">true = fill the frame and crop; false = fit with black bars.</param>
    public void Render(byte[] jpeg, int rotation, bool mirror, bool fill)
    {
        using var ms = new MemoryStream(jpeg, writable: false);
        using var src = new Bitmap(ms);

        bool sideways = rotation % 180 != 0;
        float srcW = sideways ? src.Height : src.Width;
        float srcH = sideways ? src.Width : src.Height;
        float scale = fill ? Math.Max(Width / srcW, Height / srcH) : Math.Min(Width / srcW, Height / srcH);

        _g.ResetTransform();
        _g.Clear(Color.Black);
        _g.TranslateTransform(Width / 2f, Height / 2f);
        if (mirror) _g.ScaleTransform(-1, 1);
        _g.RotateTransform(rotation);
        _g.ScaleTransform(scale, scale);
        _g.DrawImage(src,
            new Rectangle(-src.Width / 2, -src.Height / 2, src.Width, src.Height),
            0, 0, src.Width, src.Height, GraphicsUnit.Pixel, _edgeClamp);
        DrawWatermark();
    }

    /// <summary>
    /// Sets the watermark (null = none). <paramref name="size"/> is its width as a fraction of the
    /// output width; <paramref name="opacity"/> is 0–1. The caller keeps ownership of <paramref name="source"/>.
    /// </summary>
    public void SetWatermark(Bitmap? source, WatermarkPosition position, float size, float opacity)
    {
        _wmSource = source;
        _wmPosition = position;
        _wmSize = size;
        _wmOpacity = opacity;
        _wmCache?.Dispose();
        _wmCache = null;
    }

    /// <summary>Drawn last and without the mirror transform, so the text in a logo stays readable.</summary>
    private void DrawWatermark()
    {
        if (_wmSource is null) return;
        _wmCache ??= BuildWatermark(_wmSource);

        int w = _wmCache.Width, h = _wmCache.Height;
        int m = (int)(Height * 0.04f);
        var (x, y) = _wmPosition switch
        {
            WatermarkPosition.TopLeft => (m, m),
            WatermarkPosition.TopRight => (Width - w - m, m),
            WatermarkPosition.BottomLeft => (m, Height - h - m),
            WatermarkPosition.BottomRight => (Width - w - m, Height - h - m),
            _ => ((Width - w) / 2, (Height - h) / 2),
        };

        _g.ResetTransform();
        var oldMode = _g.CompositingMode;
        _g.CompositingMode = CompositingMode.SourceOver; // respect the PNG's transparency
        _g.DrawImage(_wmCache, new Rectangle(x, y, w, h));
        _g.CompositingMode = oldMode;
    }

    private Bitmap BuildWatermark(Bitmap src)
    {
        float w = Width * _wmSize;
        float h = w * src.Height / src.Width;
        float maxH = Height * 0.9f;
        if (h > maxH)
        {
            w *= maxH / h;
            h = maxH;
        }
        int iw = Math.Max(1, (int)Math.Round(w)), ih = Math.Max(1, (int)Math.Round(h));

        var bmp = new Bitmap(iw, ih, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(_wmOpacity, 0f, 1f) });
        attrs.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(src, new Rectangle(0, 0, iw, ih), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return bmp;
    }

    /// <summary>Draws a black frame with a message (shown in Zoom/Teams while the phone isn't connected).</summary>
    public void RenderMessage(string message)
    {
        _g.ResetTransform();
        _g.Clear(Color.FromArgb(20, 20, 20));
        var oldMode = _g.CompositingMode;
        _g.CompositingMode = CompositingMode.SourceOver;
        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        // Small and low in the frame: keeps the middle clean (the app's preview puts its own card there).
        using var font = new Font("Segoe UI", Height / 32f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(150, 155, 165));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
        _g.DrawString(message, font, brush, new RectangleF(0, 0, Width, Height * 0.92f), format);
        _g.CompositingMode = oldMode;
        DrawWatermark();
    }

    /// <summary>Gives temporary access to the packed BGR top-down pixels.</summary>
    public unsafe void UsePixels(Action<IntPtr> use)
    {
        var data = _target.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int rowBytes = Width * 3;
            if (data.Stride == rowBytes)
            {
                use(data.Scan0);
                return;
            }
            // Only reachable if Width*3 isn't a multiple of 4 (softcam requires width % 4 == 0 anyway).
            byte[] packed = new byte[rowBytes * Height];
            fixed (byte* dst = packed)
            {
                byte* srcRow = (byte*)data.Scan0;
                for (int y = 0; y < Height; y++)
                    Buffer.MemoryCopy(srcRow + (long)y * data.Stride, dst + (long)y * rowBytes, rowBytes, rowBytes);
                use((IntPtr)dst);
            }
        }
        finally
        {
            _target.UnlockBits(data);
        }
    }

    /// <summary>A small copy of the current frame for the on-screen preview.</summary>
    public Bitmap CreatePreview(int maxWidth)
    {
        int w = Math.Min(maxWidth, Width);
        int h = Math.Max(1, (int)Math.Round(w * (double)Height / Width));
        return new Bitmap(_target, new Size(w, h));
    }

    public void Dispose()
    {
        _g.Dispose();
        _target.Dispose();
        _wmCache?.Dispose();
        _edgeClamp.Dispose();
    }
}
