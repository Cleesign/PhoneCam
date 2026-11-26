using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace PhoneCamDesktop;

/// <summary>
/// Decodes JPEG frames from the phone and draws them — rotated, mirrored, cropped or letterboxed —
/// into a fixed-size 24-bit BGR bitmap that is handed to the virtual camera.
/// </summary>
internal sealed class FrameRenderer : IDisposable
{
    private readonly Bitmap _target;
    private readonly Graphics _g;
    private readonly ImageAttributes _edgeClamp = new();

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
    }

    /// <summary>Draws a black frame with a message (shown in Zoom/Teams while the phone isn't connected).</summary>
    public void RenderMessage(string message)
    {
        _g.ResetTransform();
        _g.Clear(Color.FromArgb(20, 20, 20));
        var oldMode = _g.CompositingMode;
        _g.CompositingMode = CompositingMode.SourceOver;
        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", Height / 24f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(200, 200, 200));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        _g.DrawString(message, font, brush, new RectangleF(0, 0, Width, Height), format);
        _g.CompositingMode = oldMode;
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
        _edgeClamp.Dispose();
    }
}
