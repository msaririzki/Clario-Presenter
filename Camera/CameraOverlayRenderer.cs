using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace Clario_Presenter.Camera;

public static class CameraOverlayRenderer
{
    public static void Draw(CanvasDrawingSession session, CameraCaptureService camera,
        CameraRectangle bounds, bool mirror)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var rect = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var radius = (float)(bounds.Width * 0.035);
        session.FillRoundedRectangle(rect, radius, radius, Color.FromArgb(255, 14, 17, 22));
        using (var clip = CanvasGeometry.CreateRoundedRectangle(session.Device, rect, radius, radius))
        using (session.CreateLayer(1, clip))
        {
            if (!camera.Draw(session, bounds, mirror))
            {
                using var format = new CanvasTextFormat
                {
                    FontSize = (float)(bounds.Width / 24),
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center
                };
                session.DrawText("Kamera tidak tersedia", rect, Color.FromArgb(255, 156, 167, 183), format);
            }
        }
        session.DrawRoundedRectangle(rect, radius, radius, Color.FromArgb(255, 156, 167, 183),
            Math.Max(1, (float)(bounds.Width / 240)));
    }
}
