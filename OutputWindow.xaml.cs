using Clario_Presenter.Capture;
using Clario_Presenter.Services;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace Clario_Presenter;

public sealed partial class OutputWindow : Window
{
    private readonly CaptureSessionService _capture;
    private readonly DisplayTarget _target;
    private readonly bool _previewMode;
    private readonly CaptureScaleMode _scaleMode;
    private bool _closed;

    public nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public OutputWindow(CaptureSessionService capture, DisplayTarget target, bool previewMode,
        CaptureScaleMode scaleMode)
    {
        InitializeComponent();
        _capture = capture;
        _target = target;
        _previewMode = previewMode;
        _scaleMode = previewMode ? CaptureScaleMode.Fit : scaleMode;
        _capture.FrameAvailable += Capture_FrameAvailable;
        Closed += OutputWindow_Closed;
        ConfigureWindow(target, previewMode);
    }

    public void ApplyPlacement()
    {
        if (_previewMode)
        {
            AppWindow.Resize(new SizeInt32(960, 540));
            return;
        }

        AppWindow.Move(new PointInt32(_target.Bounds.Left + 24, _target.Bounds.Top + 24));
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    public void SetMode(ClientOutputMode mode, string? detail = null)
    {
        if (mode is ClientOutputMode.Live or ClientOutputMode.Frozen)
        {
            PrivacySlate.Visibility = Visibility.Collapsed;
            return;
        }

        PrivacySlate.Visibility = Visibility.Visible;
        SlateTitle.Text = mode == ClientOutputMode.SourceLost ? "Sumber terputus" : "Presentasi dijeda";
        SlateSubtitle.Text = detail ?? (mode == ClientOutputMode.SourceLost
            ? "Pilih ulang jendela sumber di laptop presenter"
            : "Mohon tunggu sebentar");
    }

    private void ConfigureWindow(DisplayTarget target, bool previewMode)
    {
        AppWindow.SetIcon("Assets/AppIcon.ico");
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = !previewMode;
            presenter.IsResizable = previewMode;
            presenter.IsMaximizable = previewMode;
            presenter.IsMinimizable = previewMode;
            presenter.SetBorderAndTitleBar(previewMode, previewMode);
        }

        if (previewMode) AppWindow.Resize(new SizeInt32(960, 540));
    }

    private void Capture_FrameAvailable(object? sender, EventArgs e)
    {
        if (!_closed) OutputCanvas.Invalidate();
    }

    private void OutputCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args) =>
        _capture.Draw(sender, args, _scaleMode);

    private void OutputRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) Close();
    }

    private void OutputWindow_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _capture.FrameAvailable -= Capture_FrameAvailable;
        OutputCanvas.RemoveFromVisualTree();
    }
}

public enum ClientOutputMode
{
    Live,
    Frozen,
    Privacy,
    SourceLost
}
