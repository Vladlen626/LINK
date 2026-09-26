using System.Drawing;
using System.Drawing.Drawing2D;

namespace Link.Desktop.Windows;

public static class LinkIconFactory
{
    // The jellyfish of LinkGlyphTemplate (Themes/Controls.xaml) on the same 24-unit grid: change both together.
    private static readonly float[] Legs =
    [
        6.5f, 13.4f, 4.2f, 17.6f, 9.75f, 13.9f, 8.9f, 18.3f,
        17.5f, 13.4f, 19.8f, 17.6f, 14.25f, 13.9f, 15.1f, 18.3f
    ];

    public static Icon CreateTrayIcon()
    {
        // Drawn at the tray's own size: a scaled icon blurs the eyes.
        var size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        graphics.ScaleTransform(size / 24f, size / 24f);
        DrawGlyph(graphics);

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static void DrawGlyph(Graphics graphics)
    {
        using var body = new SolidBrush(Color.FromArgb(236, 106, 38));
        using var leg = new Pen(body, 2.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var eye = new SolidBrush(Color.FromArgb(43, 24, 16));

        for (var i = 0; i < Legs.Length; i += 4) graphics.DrawLine(leg, Legs[i], Legs[i + 1], Legs[i + 2], Legs[i + 3]);
        // FillRoundedRectangle takes the corners' diameter, not their radius.
        graphics.FillRoundedRectangle(body, new RectangleF(3.25f, 4f, 17.5f, 10.9f), new SizeF(7.6f, 7.6f));
        graphics.FillRoundedRectangle(eye, new RectangleF(7.5f, 6.7f, 2.6f, 4.4f), new SizeF(2.6f, 2.6f));
        graphics.FillRoundedRectangle(eye, new RectangleF(13.9f, 6.7f, 2.6f, 4.4f), new SizeF(2.6f, 2.6f));
    }
}
