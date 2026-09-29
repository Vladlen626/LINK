using System.Drawing;
using System.Drawing.Drawing2D;
using Link.Desktop.Models;

namespace Link.Desktop.Windows;

public static class LinkIconFactory
{
    // The jellyfish of LinkGlyphTemplate (Themes/Controls.xaml) on the same 24-unit grid: change both together.
    private static readonly float[] Legs =
    [
        6.5f, 13.4f, 4.2f, 17.6f, 9.75f, 13.9f, 8.9f, 18.3f,
        17.5f, 13.4f, 19.8f, 17.6f, 14.25f, 13.9f, 15.1f, 18.3f
    ];
    private static readonly Color Body = Color.FromArgb(236, 106, 38);
    private static readonly Color Eye = Color.FromArgb(43, 24, 16);

    /// <summary>The tray icon for a state: the jellyfish, with a dot in the corner unless Link is idle.</summary>
    public static Icon CreateTrayIcon(LinkState state)
    {
        // Drawn at the tray's own size: a scaled icon blurs the eyes.
        var size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        graphics.ScaleTransform(size / 24f, size / 24f);
        DrawGlyph(graphics);
        if (BadgeColor(state) is { } badge) DrawBadge(graphics, badge);

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

    // The overlay's own colors: aqua while it works, red when it waits for the user, green for a ready answer.
    private static Color? BadgeColor(LinkState state) => state switch
    {
        LinkState.Working => Color.FromArgb(74, 207, 223),
        LinkState.NeedsDecision => Color.FromArgb(248, 81, 73),
        LinkState.Done => Color.FromArgb(63, 185, 80),
        _ => null
    };

    private static void DrawGlyph(Graphics graphics)
    {
        using var body = new SolidBrush(Body);
        using var leg = new Pen(body, 2.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var eye = new SolidBrush(Eye);

        for (var i = 0; i < Legs.Length; i += 4) graphics.DrawLine(leg, Legs[i], Legs[i + 1], Legs[i + 2], Legs[i + 3]);
        // FillRoundedRectangle takes the corners' diameter, not their radius.
        graphics.FillRoundedRectangle(body, new RectangleF(3.25f, 4f, 17.5f, 10.9f), new SizeF(7.6f, 7.6f));
        graphics.FillRoundedRectangle(eye, new RectangleF(7.5f, 6.7f, 2.6f, 4.4f), new SizeF(2.6f, 2.6f));
        graphics.FillRoundedRectangle(eye, new RectangleF(13.9f, 6.7f, 2.6f, 4.4f), new SizeF(2.6f, 2.6f));
    }

    private static void DrawBadge(Graphics graphics, Color color)
    {
        // A dark ring keeps the dot apart from the orange legs on a light or a dark taskbar.
        using var ring = new SolidBrush(Eye);
        using var dot = new SolidBrush(color);
        graphics.FillEllipse(ring, 12.5f, 12.5f, 11.5f, 11.5f);
        graphics.FillEllipse(dot, 14.5f, 14.5f, 7.5f, 7.5f);
    }
}
