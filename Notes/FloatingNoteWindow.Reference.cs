using Clario_Presenter.Capture;
using Clario_Presenter.Services;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace Clario_Presenter.Notes;

internal sealed partial class FloatingNoteWindow
{
    private const int ReferenceTimer = 3;
    private WindowSource? _reference;
    private CaptureSessionService? _referenceCapture;
    private bool _referenceDirty;
    private bool _referenceFailed;
    private bool _referenceUnavailable;
    private float _zoom = 1;
    private float _referenceX = .5f;
    private float _referenceY = .5f;
    internal bool HasReferenceFrame => !_referenceUnavailable && _referenceCapture?.HasFrame == true;
    internal float ReferenceZoom => _zoom;

    internal void SetReference(WindowSource? source)
    {
        if (_reference == source) return;
        StopReferenceCapture();
        _reference = source;
        _zoom = 1;
        _referenceX = _referenceY = .5f;
        _indexOpen = false;
    }

    private void StartReferenceCapture()
    {
        if (_reference is null || _referenceCapture is not null) return;
        _referenceFailed = false;
        _referenceUnavailable = true;
        var capture = new CaptureSessionService();
        _referenceCapture = capture;
        capture.FrameAvailable += (_, _) => _referenceDirty = true;
        capture.SourceClosed += (_, _) => _referenceFailed = true;
        capture.CaptureFailed += (_, _) => _referenceFailed = true;
        try
        {
            if (WindowCatalogService.GetProcessId(_reference.Handle) != _reference.ProcessId)
                throw new InvalidOperationException("Jendela sumber sudah ditutup.");
            capture.Start(_reference.Handle, captureCursor: false);
            if (SetTimer(_window, ReferenceTimer, 100, 0) == 0) throw new System.ComponentModel.Win32Exception();
        }
        catch
        {
            StopReferenceCapture();
            _referenceFailed = true;
        }
    }

    private void StopReferenceCapture()
    {
        KillTimer(_window, ReferenceTimer);
        _referenceCapture?.Dispose();
        _referenceCapture = null;
        _referenceDirty = false;
    }

    private void RefreshReference()
    {
        if (!_shown || _reference is null || !KeepVisibleIfProtected()) return;
        if (_referenceFailed || WindowCatalogService.GetProcessId(_reference.Handle) != _reference.ProcessId)
        {
            StopReferenceCapture();
            _referenceFailed = true;
            Refresh();
            return;
        }
        var unavailable = !WindowCatalogService.IsWindowPresentable(_reference.Handle) || _referenceCapture?.HasFrame != true;
        if (!_referenceDirty && unavailable == _referenceUnavailable) return;
        _referenceDirty = false;
        _referenceUnavailable = unavailable;
        Refresh();
    }

    private void DrawReference(CanvasDrawingSession session, Rect viewport)
    {
        if (_referenceFailed || !HasReferenceFrame)
        {
            using var format = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontSize = 14 };
            session.DrawText(_referenceFailed ? "Tampilan terputus. Pilih kembali jendela sumber."
                : "Menunggu tampilan. Pastikan jendela sumber terbuka dan tidak diminimalkan.",
                viewport, Windows.UI.Color.FromArgb(255, 183, 196, 213), format);
            return;
        }
        _referenceCapture!.DrawReference(session, viewport, _zoom, _referenceX, _referenceY);
    }

    private void SetZoom(float zoom)
    {
        _zoom = Math.Clamp(zoom, 1, 3);
        if (_zoom == 1) _referenceX = _referenceY = .5f;
        Refresh();
    }

    private void PanReference(float horizontal, float vertical)
    {
        if (_zoom <= 1) return;
        _referenceX = Math.Clamp(_referenceX + horizontal, 0, 1);
        _referenceY = Math.Clamp(_referenceY + vertical, 0, 1);
        Refresh();
    }
}
