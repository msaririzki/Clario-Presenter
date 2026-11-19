using System.Runtime.InteropServices;
using Clario_Presenter.Capture;
using Clario_Presenter.Recording;
using Clario_Presenter.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace Clario_Presenter;

public sealed partial class MainPage
{
    private const uint ExcludeFromCapture = 0x11;
    private bool _settingsDialogOpen;

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsDialogOpen || _handoff) return;
        _settingsDialogOpen = true;
        try
        {
            await SyncStudioPreviewAsync();
            using var sourcePreview = await CaptureSettingsBackgroundAsync();
            if (!_isLoaded) return;

            var dialog = new SettingsDialog(_settingsService.Current, XamlRoot, sourcePreview)
            {
                RequestedTheme = ActualTheme
            };
            dialog.ConfigureUpdates(_updates, () => UpdatesAllowed, () => _backupOnClose = Updates.UpdateService.InstalledCopy,
                ReferenceEquals(sender, OpenUpdateButton));
            await dialog.ShowAsync();
            SettingsButton.Focus(FocusState.Programmatic);
            if (!dialog.IsSaved) return;

            try { _settingsService.Save(dialog.GetSettings()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                FooterStatusText.Text = $"Pengaturan belum tersimpan. {error.Message}";
                return;
            }
            if (dialog.ApplyUpdate) { await FinishForUpdateAsync(true); return; }
            var settings = _settingsService.Current;
            FooterStatusText.Text = $"Pengaturan tersimpan · 1080p {settings.RecordingFrameRate} fps · {settings.RecordingBitrateMbps} Mbps"
                + (settings.CameraEnabled ? " · kamera aktif untuk rekaman berikutnya" : string.Empty);
        }
        finally
        {
            _settingsDialogOpen = false;
            await SyncStudioPreviewAsync();
        }
    }

    private async Task<CanvasRenderTarget?> CaptureSettingsBackgroundAsync()
    {
        try
        {
            if (_isLive)
                return !_isPrivacy && _capture is { HasFrame: true } live
                    ? CreateSettingsBackground(live, _outputScaleMode, _outputAdaptiveZoom)
                    : null;

            var monitor = IsSafeMirrorMode ? SourceMonitorComboBox.SelectedItem as DisplayTarget : null;
            var source = IsSafeMirrorMode ? null : SourceComboBox.SelectedItem as WindowSource;
            if (monitor is null && source is null) return null;
            if (monitor is not null && WindowCatalogService.HasPrivateWindowOnDisplay(monitor.Bounds, _privateWindows))
                return null;
            if (source is not null && WindowCatalogService.IsInWindowFamily(source.Handle, _privateWindows))
                return null;

            var window = GetPresenterWindowHandle();
            var affinity = 0u;
            var restoreAffinity = monitor is not null && window != nint.Zero
                && GetWindowDisplayAffinity(window, out affinity) && affinity != ExcludeFromCapture
                && SetWindowDisplayAffinity(window, ExcludeFromCapture);
            try
            {
                using var capture = new CaptureSessionService();
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                capture.FrameAvailable += (_, _) => ready.TrySetResult(true);
                capture.SourceClosed += (_, _) => ready.TrySetResult(false);
                capture.CaptureFailed += (_, _) => ready.TrySetResult(false);
                if (monitor is not null) capture.StartMonitor(monitor.Handle, captureCursor: false);
                else capture.Start(source!.Handle, captureCursor: false);

                if (!await ready.Task.WaitAsync(TimeSpan.FromMilliseconds(800)) || _isPrivacy)
                    return null;
                if (monitor is not null && WindowCatalogService.HasPrivateWindowOnDisplay(monitor.Bounds, _privateWindows))
                    return null;
                return CreateSettingsBackground(capture, monitor is not null ? CaptureScaleMode.Fit : ResolveOutputScaleMode(), 1);
            }
            finally
            {
                if (restoreAffinity) SetWindowDisplayAffinity(window, affinity);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Settings source preview: {exception.Message}");
            return null;
        }
    }

    private static CanvasRenderTarget CreateSettingsBackground(CaptureSessionService capture, CaptureScaleMode scaleMode, float zoom)
    {
        const uint width = OutputRecordingService.OutputWidth / 4;
        const uint height = OutputRecordingService.OutputHeight / 4;
        var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        try
        {
            using var drawing = target.CreateDrawingSession();
            capture.DrawRecordingFrame(drawing, width, height, scaleMode, zoom);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private void ExcludePresenterFromScreenCapture()
    {
        var window = GetPresenterWindowHandle();
        if (window == nint.Zero || !GetWindowDisplayAffinity(window, out var previous)) return;
        if (previous == ExcludeFromCapture)
        {
            _presenterExcludedFromCapture = true;
            return;
        }

        if (SetWindowDisplayAffinity(window, ExcludeFromCapture))
        {
            _presenterPreviousCaptureAffinity = previous;
            _presenterExcludedFromCapture = true;
        }
    }

    private void RestorePresenterCaptureAffinity()
    {
        if (_presenterPreviousCaptureAffinity is { } previous)
            SetWindowDisplayAffinity(GetPresenterWindowHandle(), previous);
        _presenterPreviousCaptureAffinity = null;
        _presenterExcludedFromCapture = false;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}
