using Microsoft.Graphics.Canvas;

namespace Clario_Presenter.Capture;

/// <summary>The retained clean output consumed by the video encoder.</summary>
public interface IRecordingFrameSource
{
    bool HasFrame { get; }
    void DrawRecordingFrame(CanvasDrawingSession session, float width, float height,
        CaptureScaleMode scaleMode = CaptureScaleMode.Fit, float adaptiveZoom = 1.15f);
}
