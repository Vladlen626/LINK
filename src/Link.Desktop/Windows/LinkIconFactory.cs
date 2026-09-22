using System.Drawing;
using System.Drawing.Drawing2D;

namespace Link.Desktop.Windows;

public static class LinkIconFactory
{
    public static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var pen = new Pen(Color.FromArgb(255, 80, 210, 218), 3.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var highlight = new Pen(Color.FromArgb(145, 255, 255, 255), 1.0f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        DrawLink(graphics, pen, 4, 10);
        DrawLink(graphics, highlight, 4, 9.4f);
        DrawLink(graphics, pen, 13, 14);
        DrawLink(graphics, highlight, 13, 13.4f);

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

    private static void DrawLink(Graphics graphics, Pen pen, float x, float y)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(x + 7.5f, y + 4.5f);
        graphics.RotateTransform(-35);
        graphics.TranslateTransform(-(x + 7.5f), -(y + 4.5f));
        using var path = RoundedCapsule(new RectangleF(x, y, 15, 9));
        graphics.DrawPath(pen, path);
        graphics.Restore(state);
    }

    private static GraphicsPath RoundedCapsule(RectangleF bounds)
    {
        var radius = bounds.Height;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, radius, radius, 90, 180);
        path.AddArc(bounds.Right - radius, bounds.Top, radius, radius, 270, 180);
        path.CloseFigure();
        return path;
    }
}
