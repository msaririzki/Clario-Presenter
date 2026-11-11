namespace Clario_Presenter.Camera;

public enum CameraOverlayPosition { BottomRight, BottomLeft, TopRight, TopLeft }

public sealed record CameraOverlayOptions
{
    public bool Enabled { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public CameraOverlayPosition Position { get; init; } = CameraOverlayPosition.BottomRight;
    public int WidthPercent { get; init; } = 24;
    public bool Mirror { get; init; }
}

public readonly record struct CameraRectangle(double X, double Y, double Width, double Height);

public static class CameraOverlayLayout
{
    public static CameraRectangle GetBounds(double outputWidth, double outputHeight, CameraOverlayOptions options)
    {
        if (outputWidth <= 0 || outputHeight <= 0) return default;
        var scale = Math.Min(outputWidth / 1920, outputHeight / 1080);
        var margin = 32 * scale;
        // Reserve space for the taskbar even when the camera is in a bottom corner.
        var bottomMargin = 64 * scale;
        var width = Math.Min(outputWidth * Math.Clamp(options.WidthPercent, 15, 35) / 100,
            (outputHeight - margin - bottomMargin) * 16 / 9);
        var height = width * 9 / 16;
        var left = options.Position is CameraOverlayPosition.BottomLeft or CameraOverlayPosition.TopLeft;
        var top = options.Position is CameraOverlayPosition.TopLeft or CameraOverlayPosition.TopRight;
        return new CameraRectangle(left ? margin : outputWidth - margin - width,
            top ? margin : outputHeight - bottomMargin - height, width, height);
    }

    public static CameraRectangle Fit(double sourceWidth, double sourceHeight, CameraRectangle destination)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return default;
        var scale = Math.Min(destination.Width / sourceWidth, destination.Height / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        return new CameraRectangle(destination.X + (destination.Width - width) / 2,
            destination.Y + (destination.Height - height) / 2, width, height);
    }
}
