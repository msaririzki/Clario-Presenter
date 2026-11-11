using Clario_Presenter.Services;

namespace Clario_Presenter.Capture;

public readonly record struct DesktopWindowCandidate(nint Handle, DisplayBounds Bounds);

public static class DesktopWindowPlan
{
    // Input is front-to-back. Private windows are already filtered out, so they
    // never occlude the public windows underneath them in the recorded desktop.
    public static IReadOnlyList<DesktopWindowCandidate> Select(
        IEnumerable<DesktopWindowCandidate> windows, DisplayBounds workArea, int maximumLayers = 8)
    {
        var visible = new List<DesktopWindowCandidate>();
        foreach (var window in windows)
        {
            var clipped = Clip(window.Bounds, workArea);
            if (clipped.Width <= 0 || clipped.Height <= 0) continue;
            if (visible.Any(front => Contains(Clip(front.Bounds, workArea), clipped))) continue;
            visible.Add(window);
            if (visible.Count >= maximumLayers) break;
        }
        visible.Reverse();
        return visible;
    }

    public static bool Intersects(DisplayBounds a, DisplayBounds b) => Clip(a, b) is { Width: > 0, Height: > 0 };

    private static bool Contains(DisplayBounds outer, DisplayBounds inner) => outer.Left <= inner.Left
        && outer.Top <= inner.Top && outer.Left + outer.Width >= inner.Left + inner.Width
        && outer.Top + outer.Height >= inner.Top + inner.Height;

    private static DisplayBounds Clip(DisplayBounds a, DisplayBounds b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        return new DisplayBounds(left, top,
            Math.Max(0, Math.Min(a.Left + a.Width, b.Left + b.Width) - left),
            Math.Max(0, Math.Min(a.Top + a.Height, b.Top + b.Height) - top));
    }
}
