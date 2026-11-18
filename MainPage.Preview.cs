using System.Numerics;
using Clario_Presenter.Audio;
using Clario_Presenter.Camera;
using Clario_Presenter.Recording;
using Clario_Presenter.Settings;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clario_Presenter;

public sealed partial class MainPage
{
    private readonly DispatcherTimer _studioTimer = new() { Interval = TimeSpan.FromMilliseconds(67) };
    private readonly SemaphoreSlim _studioGate = new(1);
    private PresenterSettings? _studioInputSettings;
    private CameraOverlayOptions? _studioCameraOptions;
    private CancellationTokenSource? _studioStart;
    private Task _studioChange = Task.CompletedTask;
    private AudioLevelMonitor? _studioPlayback, _studioMic;
    private CameraCaptureService? _studioCamera;
    private string? _studioCameraError;
    private long _lastMeterTick;

    private bool _studioWindowVisible;
    private bool _recordingPreviewOpen;
    private bool _studioFramePending;
    private bool RecordingPreviewHidden => _isRecordingOnlySession && _recorder is not null && !_recordingPreviewOpen;
    private bool ShowStudioInputs => _isLoaded && _studioWindowVisible && PreviewPanel.Visibility == Visibility.Visible && _isLive && !_isPrivacy
        && !_settingsDialogOpen && !_sessionTransition && !RecordingPreviewHidden;

    private void StudioWindow_Changed(AppWindow window, AppWindowChangedEventArgs args)
    {
        UpdateStudioWindowVisibility(window);
    }

    private void UpdateStudioWindowVisibility(AppWindow window)
    {
        var visible = window.IsVisible && window.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (_studioWindowVisible == visible) return;
        _studioWindowVisible = visible;
        if (!visible)
        {
            _recordingPreviewOpen = false;
            if (_isRecordingOnlySession && _isLive && _recorder is null && !_sessionTransition && !_settingsDialogOpen)
            {
                StopPresentation();
                return;
            }
        }
        if (_isLoaded) UpdateSessionActions();
        _ = SyncStudioPreviewAsync();
        if (visible && _isLive)
        {
            UpdatePreview();
            LivePreviewCanvas.Invalidate();
        }
    }

    private static CameraOverlayOptions PreviewCameraOptions(PresenterSettings settings) => new()
    {
        Enabled = settings.CameraEnabled, DeviceId = settings.CameraDeviceId,
        Position = (CameraOverlayPosition)settings.CameraPosition, CustomPosition = settings.CameraCustomPosition,
        WidthPercent = settings.CameraWidthPercent, Mirror = settings.CameraMirror
    };

    private Task SyncStudioPreviewAsync()
    {
        var show = ShowStudioInputs;
        StudioMeters.Visibility = _isLive && !RecordingPreviewHidden ? Visibility.Visible : Visibility.Collapsed;
        _recorder?.SetPreviewMeters(show);
        if (_isLoaded && _studioWindowVisible && _recorder is not null) _sessionTimer.Start();
        else _sessionTimer.Stop();
        if (show) _studioTimer.Start();
        else _studioTimer.Stop();

        // While recording the preview reads the recorder's existing camera and PCM tracks.
        var desired = show && _recorder is null ? _settingsService.Current : null;
        if (desired != _studioInputSettings)
        {
            _studioInputSettings = desired;
            _studioStart?.Cancel();
            _studioStart?.Dispose();
            _studioStart = desired is null ? null : new();
            _studioChange = ChangeStudioInputsAsync(desired, _studioStart?.Token ?? default);
        }
        UpdateStudioMeters();
        return _studioChange;
    }

    private async Task ChangeStudioInputsAsync(PresenterSettings? settings, CancellationToken token)
    {
        await _studioGate.WaitAsync();
        try
        {
            if (token.IsCancellationRequested) return;
            var camera = _studioCamera;
            var playback = _studioPlayback;
            var mic = _studioMic;
            _studioCamera = null;
            _studioPlayback = _studioMic = null;
            _studioCameraOptions = null;
            _studioCameraError = null;
            if (camera is not null) await camera.DisposeAsync();
            if (playback is not null) await playback.DisposeAsync();
            if (mic is not null) await mic.DisposeAsync();
            if (settings is null || token.IsCancellationRequested) return;
            if (settings.ComputerAudioEnabled) _studioPlayback = new(settings.PlaybackDeviceId, true);
            if (settings.MicrophoneEnabled) _studioMic = new(settings.MicrophoneDeviceId, false, settings.MicrophoneSpeechProfile);
            if (settings.CameraEnabled)
            {
                var candidate = new CameraCaptureService();
                try
                {
                    await candidate.StartAsync(settings.CameraDeviceId, token);
                    token.ThrowIfCancellationRequested();
                    _studioCamera = candidate;
                    _studioCameraOptions = PreviewCameraOptions(settings);
                }
                finally
                {
                    if (_studioCamera != candidate) await candidate.DisposeAsync();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _studioCameraError = ex.Message;
        }
        finally { _studioGate.Release(); }
    }

    private void StudioTimer_Tick(object? sender, object args)
    {
        if (!ShowStudioInputs) return;
        if (_studioFramePending || _studioCamera is not null || _recorder?.IncludesCamera == true)
        {
            _studioFramePending = false;
            if (ClientStateOverlay.Visibility == Visibility.Visible) UpdatePreview();
            LivePreviewCanvas.Invalidate();
        }
        if (Environment.TickCount64 - _lastMeterTick < 100) return;
        _lastMeterTick = Environment.TickCount64;
        UpdateStudioMeters();
    }

    private void UpdateStudioMeters()
    {
        var show = ShowStudioInputs;
        var idle = _isPrivacy ? "Dibisukan" : "Preview berhenti";
        var settings = _settingsService.Current;
        void ShowLevel(bool loopback, ProgressBar bar, TextBlock status)
        {
            var reading = _recorder is not null ? _recorder.ReadPreviewLevel(loopback)
                : (loopback ? _studioPlayback : _studioMic)?.TakeReading();
            var enabled = _recorder is not null ? reading.HasValue
                : loopback ? settings.ComputerAudioEnabled : settings.MicrophoneEnabled;
            var text = !enabled ? "Nonaktif" : !show ? idle : reading?.Error
                ?? (reading?.IsReady == true ? null : "Menghubungkan…");
            if (text is not null)
            {
                bar.Value = 0;
                status.Text = text;
                return;
            }
            var peak = reading.GetValueOrDefault().Peak;
            var db = peak > 0 ? 20 * Math.Log10(peak / 32768d) : -60;
            bar.Value = Math.Max(Math.Clamp((db + 60) / 60 * 100, 0, 100), bar.Value - 14);
            bar.Foreground = db >= -1 ? PrivacyBrush : db >= -6 ? FreezeBrush : LiveBrush;
            status.Text = db <= -60 ? "Senyap" : $"{db:0} dBFS";
        }
        ShowLevel(true, StudioPlaybackBar, StudioPlaybackText);
        ShowLevel(false, StudioMicBar, StudioMicText);
        StudioCameraText.Text = !(_recorder?.IncludesCamera ?? settings.CameraEnabled) ? "Kamera nonaktif"
            : !show ? (_isPrivacy ? "Kamera disembunyikan" : "Kamera menunggu preview")
            : _recorder?.CameraWarning ?? _studioCameraError ?? _studioCamera?.Warning
            ?? (_recorder?.IncludesCamera == true || _studioCamera is not null ? "Kamera aktif" : "Menyalakan kamera…");
    }

    private void DrawStudioPreview(CanvasControl canvas, CanvasDrawEventArgs args)
    {
        if (_capture is null || !ShowStudioInputs) return;
        const float width = OutputRecordingService.OutputWidth, height = OutputRecordingService.OutputHeight;
        var scale = (float)Math.Min(canvas.ActualWidth / width, canvas.ActualHeight / height);
        if (scale <= 0) return;
        var session = args.DrawingSession;
        var saved = session.Transform;
        try
        {
            session.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(
                (float)(canvas.ActualWidth - width * scale) / 2, (float)(canvas.ActualHeight - height * scale) / 2) * saved;
            using var clip = session.CreateLayer(1, new Windows.Foundation.Rect(0, 0, width, height));
            _capture.DrawRecordingFrame(session, width, height, _outputScaleMode, _outputAdaptiveZoom);
            if (_recorder is not null) _recorder.DrawPreviewCamera(session);
            else if (_studioCamera is not null && _studioCameraOptions is { } options)
                CameraOverlayRenderer.Draw(session, _studioCamera, CameraOverlayLayout.GetBounds(width, height, options), options.Mirror);
        }
        finally { session.Transform = saved; }
    }
}
