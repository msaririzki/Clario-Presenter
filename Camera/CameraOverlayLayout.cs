namespace Clario_Presenter.Camera;

public enum CameraOverlayPosition { BottomRight, BottomLeft, TopRight, TopLeft, Custom }

public readonly record struct CameraOverlayPoint(double X, double Y)
{
    public CameraOverlayPoint Clamped() => new(ClampAxis(X), ClampAxis(Y));

    private static double ClampAxis(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;
}

public sealed record CameraOverlayOptions
{
    public bool Enabled { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public CameraOverlayPosition Position { get; init; } = CameraOverlayPosition.BottomRight;
    public CameraOverlayPoint CustomPosition { get; init; } = new(1, 1);
    public int WidthPercent { get; init; } = 24;
    public bool Mirror { get; init; }
}

public readonly record struct CameraRectangle(double X, double Y, double Width, double Height);

public static class CameraOverlayLayout
{
    public const int MinWidthPercent = 5;
    public const int MaxWidthPercent = 90;
    private const double RecordingWidth = 1920;
    private const double RecordingHeight = 1080;

    public static CameraRectangle GetBounds(double outputWidth, double outputHeight, CameraOverlayOptions options)
    {
        if (outputWidth <= 0 || outputHeight <= 0) return default;
        var area = options.Position == CameraOverlayPosition.Custom
            ? new CameraRectangle(0, 0, outputWidth, outputHeight)
            : GetPresetArea(outputWidth, outputHeight);
        var point = options.Position switch
        {
            CameraOverlayPosition.BottomLeft => new CameraOverlayPoint(0, 1),
            CameraOverlayPosition.TopRight => new CameraOverlayPoint(1, 0),
            CameraOverlayPosition.TopLeft => new CameraOverlayPoint(0, 0),
            CameraOverlayPosition.Custom => options.CustomPosition.Clamped(),
            _ => new CameraOverlayPoint(1, 1)
        };
        return PlaceInArea(area, outputWidth, options.WidthPercent, point);
    }

    public static CameraOverlayPoint GetCustomPosition(double outputWidth, double outputHeight,
        CameraOverlayOptions options, double x, double y)
    {
        if (outputWidth <= 0 || outputHeight <= 0) return default;
        var size = PlaceInArea(new CameraRectangle(0, 0, outputWidth, outputHeight), outputWidth, options.WidthPercent, default);
        return new CameraOverlayPoint(
            outputWidth > size.Width ? x / (outputWidth - size.Width) : 0,
            outputHeight > size.Height ? y / (outputHeight - size.Height) : 0).Clamped();
    }

    public static CameraOverlayPoint MigrateInsetPosition(int widthPercent, CameraOverlayPoint previous)
    {
        var inset = PlaceInArea(GetPresetArea(RecordingWidth, RecordingHeight), RecordingWidth, widthPercent, previous.Clamped());
        return GetCustomPosition(RecordingWidth, RecordingHeight,
            new CameraOverlayOptions { WidthPercent = widthPercent }, inset.X, inset.Y);
    }

    private static CameraRectangle PlaceInArea(CameraRectangle area, double outputWidth, int widthPercent, CameraOverlayPoint point)
    {
        var width = Math.Min(outputWidth * Math.Clamp(widthPercent, MinWidthPercent, MaxWidthPercent) / 100,
            Math.Min(area.Width, area.Height * 16 / 9));
        var height = width * 9 / 16;
        return new CameraRectangle(area.X + (area.Width - width) * point.X,
            area.Y + Math.Max(0, area.Height - height) * point.Y, width, height);
    }

    private static CameraRectangle GetPresetArea(double width, double height)
    {
        var scale = Math.Min(width / RecordingWidth, height / RecordingHeight);
        var margin = 32 * scale;
        return new CameraRectangle(margin, margin, width - 2 * margin, height - 3 * margin);
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
