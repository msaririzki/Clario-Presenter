using Clario_Presenter.Services;

namespace Clario_Presenter.Capture;

/// <summary>Fixed desktop coordinates shared by mirror, protected output, and recording.</summary>
public sealed class DesktopCompositionLayout
{
    public DisplayBounds Bounds { get; }
    public DisplayBounds WorkArea { get; }
    public DisplayBounds TaskbarBounds { get; }

    public DesktopCompositionLayout(DisplayBounds bounds, DisplayBounds workArea)
    {
        Bounds = bounds;
        WorkArea = Intersect(bounds, workArea);
        var candidates = new[]
        {
            new DisplayBounds(Bounds.Left, Bounds.Top, WorkArea.Left - Bounds.Left, Bounds.Height),
            new DisplayBounds(Bounds.Left, Bounds.Top, Bounds.Width, WorkArea.Top - Bounds.Top),
            new DisplayBounds(WorkArea.Left + WorkArea.Width, Bounds.Top,
                Bounds.Left + Bounds.Width - WorkArea.Left - WorkArea.Width, Bounds.Height),
            new DisplayBounds(Bounds.Left, WorkArea.Top + WorkArea.Height, Bounds.Width,
                Bounds.Top + Bounds.Height - WorkArea.Top - WorkArea.Height)
        };
        TaskbarBounds = candidates.Where(region => region.Width > 0 && region.Height > 0)
            .OrderByDescending(region => (long)region.Width * region.Height)
            .FirstOrDefault();
    }

    public DisplayBounds ClipPublicWindow(DisplayBounds window) => Intersect(WorkArea, window);

    public (double X, double Y, double Width, double Height) MapRegion(
        DisplayBounds region, double x, double y, double width, double height) =>
        (x + (region.Left - Bounds.Left) * width / Bounds.Width,
            y + (region.Top - Bounds.Top) * height / Bounds.Height,
            region.Width * width / Bounds.Width,
            region.Height * height / Bounds.Height);

    private static DisplayBounds Intersect(DisplayBounds a, DisplayBounds b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Left + a.Width, b.Left + b.Width);
        var bottom = Math.Min(a.Top + a.Height, b.Top + b.Height);
        return new DisplayBounds(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
