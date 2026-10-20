using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Link.Desktop;

/// <summary>
/// Menus under or over their button that stay inside the button's window: left-aligned with the button while they fit,
/// otherwise moved left just enough. WPF's own placement follows Windows' handedness setting, which for the right hand
/// lines a menu's right edge up with the button's, and hung menus out of the narrow overlay.
/// </summary>
internal static class MenuPlacement
{
    public static ContextMenu Under(FrameworkElement target) => Create(target, above: false);

    public static ContextMenu Over(FrameworkElement target) => Create(target, above: true);

    private static ContextMenu Create(FrameworkElement target, bool above) => new()
    {
        PlacementTarget = target,
        Placement = PlacementMode.Custom,
        CustomPopupPlacementCallback = (popup, targetSize, _) =>
        {
            // The sizes come in device pixels; the window is measured in the target's units.
            var scale = target.ActualWidth > 0 ? targetSize.Width / target.ActualWidth : 1;
            var x = 0.0;
            if (Window.GetWindow(target) is { } window)
            {
                var left = target.TranslatePoint(default, window).X * scale;
                x = Math.Max(Math.Min(0, window.ActualWidth * scale - left - popup.Width), -left);
            }

            var below = new CustomPopupPlacement(new Point(x, targetSize.Height), PopupPrimaryAxis.Horizontal);
            var over = new CustomPopupPlacement(new Point(x, -popup.Height), PopupPrimaryAxis.Horizontal);
            return above ? [over, below] : [below, over];
        }
    };
}
