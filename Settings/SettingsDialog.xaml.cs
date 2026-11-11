using Clario_Presenter.Settings;
using Clario_Presenter.Camera;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.System;

namespace Clario_Presenter;

public sealed partial class SettingsDialog : ContentDialog
{
    private string _folderPath;
    private readonly PresenterSettings _originalSettings;
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly CancellationTokenSource _previewCancellation = new();
    private readonly DispatcherTimer _previewStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CameraCaptureService? _previewCamera;
    private bool _closing;

    public SettingsDialog(PresenterSettings settings)
    {
        InitializeComponent();
        _originalSettings = settings;
        _folderPath = settings.RecordingFolderPath;
        FrameRateComboBox.SelectedIndex = settings.RecordingFrameRate == 30 ? 1 : 0;
        BitrateComboBox.SelectedIndex = settings.RecordingBitrateMbps switch
        {
            <= 10 => 0,
            >= 28 => 2,
            _ => 1
        };
        AskLocationToggle.IsOn = settings.AskRecordingLocation;
        CameraEnabledToggle.IsOn = settings.CameraEnabled;
        CameraPositionComboBox.SelectedIndex = Math.Clamp(settings.CameraPosition, 0, 3);
        CameraSizeComboBox.SelectedIndex = settings.CameraWidthPercent <= 18 ? 0 : settings.CameraWidthPercent >= 30 ? 2 : 1;
        CameraMirrorCheckBox.IsChecked = settings.CameraMirror;
        _previewStatusTimer.Tick += PreviewStatusTimer_Tick;
        UpdateFolderLabel();
    }

    public PresenterSettings GetSettings()
    {
        var frameRate = FrameRateComboBox.SelectedIndex == 1 ? 30 : 60;
        var bitrate = BitrateComboBox.SelectedIndex switch
        {
            0 => 10,
            2 => 28,
            _ => 18
        };

        return _originalSettings with
        {
            RecordingFrameRate = frameRate,
            RecordingBitrateMbps = bitrate,
            AskRecordingLocation = AskLocationToggle.IsOn,
            RecordingFolderPath = _folderPath,
            CameraEnabled = CameraEnabledToggle.IsOn,
            CameraDeviceId = (CameraDeviceComboBox.SelectedItem as CameraDevice)?.Id ?? _originalSettings.CameraDeviceId,
            CameraPosition = Math.Clamp(CameraPositionComboBox.SelectedIndex, 0, 3),
            CameraWidthPercent = CameraSizeComboBox.SelectedIndex switch { 0 => 18, 2 => 30, _ => 24 },
            CameraMirror = CameraMirrorCheckBox.IsChecked == true
        };
    }

    private async void SettingsDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        await RefreshCamerasAsync();

    private async void RefreshCameras_Click(object sender, RoutedEventArgs e) => await RefreshCamerasAsync();

    private async Task RefreshCamerasAsync()
    {
        var selectedId = (CameraDeviceComboBox.SelectedItem as CameraDevice)?.Id ?? _originalSettings.CameraDeviceId;
        RefreshCamerasButton.IsEnabled = false;
        TestCameraButton.IsEnabled = false;
        try
        {
            var devices = await CameraCaptureService.GetDevicesAsync();
            if (_closing) return;
            CameraDeviceComboBox.ItemsSource = devices;
            CameraDeviceComboBox.SelectedItem = string.IsNullOrEmpty(selectedId)
                ? devices.FirstOrDefault()
                : devices.FirstOrDefault(device => device.Id == selectedId);
            CameraStatusText.Text = devices.Count == 0
                ? "Kamera belum ditemukan. Hubungkan webcam USB, lalu klik Perbarui daftar."
                : CameraDeviceComboBox.SelectedItem is null
                    ? "Kamera yang sebelumnya dipilih belum terhubung. Pilih kamera yang tersedia."
                    : "Klik Tes kamera untuk melihat gambarnya.";
        }
        catch (Exception exception)
        {
            if (!_closing) CameraStatusText.Text = $"Daftar kamera gagal dimuat: {exception.Message}";
        }
        finally
        {
            if (!_closing)
            {
                RefreshCamerasButton.IsEnabled = true;
                TestCameraButton.IsEnabled = CameraDeviceComboBox.Items.Count > 0;
            }
        }
    }

    private async void TestCamera_Click(object sender, RoutedEventArgs e)
    {
        await _previewGate.WaitAsync();
        try
        {
            if (_closing) return;
            TestCameraButton.IsEnabled = false;
            CameraDeviceComboBox.IsEnabled = false;
            RefreshCamerasButton.IsEnabled = false;
            if (_previewCamera is not null)
            {
                await StopCameraPreviewAsync();
                CameraStatusText.Text = "Kamera dimatikan.";
                return;
            }
            if (CameraDeviceComboBox.SelectedItem is not CameraDevice device)
                throw new InvalidOperationException("Pilih kamera terlebih dahulu.");
            CameraStatusText.Text = "Menyalakan kamera…";
            _previewCamera = new CameraCaptureService();
            _previewCamera.FrameAvailable += PreviewCamera_FrameAvailable;
            await _previewCamera.StartAsync(device.Id, _previewCancellation.Token);
            if (_closing) return;
            CameraPreviewPlaceholder.Visibility = Visibility.Collapsed;
            TestCameraButton.Content = "Stop preview";
            CameraStatusText.Text = CameraEnabledToggle.IsOn
                ? "Kamera siap. Klik Simpan untuk memakai kamera ini saat merekam."
                : "Kamera siap. Aktifkan Sertakan kamera dalam rekaman, lalu klik Simpan.";
            _previewStatusTimer.Start();
            CameraPreviewCanvas.Invalidate();
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception exception)
        {
            await StopCameraPreviewAsync();
            if (!_closing) CameraStatusText.Text = exception.Message;
        }
        finally
        {
            if (!_closing)
            {
                TestCameraButton.IsEnabled = true;
                CameraDeviceComboBox.IsEnabled = _previewCamera is null;
                RefreshCamerasButton.IsEnabled = _previewCamera is null;
            }
            _previewGate.Release();
        }
    }

    private void PreviewCamera_FrameAvailable(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closing && ReferenceEquals(sender, _previewCamera)) CameraPreviewCanvas.Invalidate();
    });

    private void CameraPreviewCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_previewCamera is not null)
            CameraOverlayRenderer.Draw(args.DrawingSession, _previewCamera,
                new CameraRectangle(2, 2, Math.Max(0, sender.ActualWidth - 4), Math.Max(0, sender.ActualHeight - 4)),
                CameraMirrorCheckBox.IsChecked == true);
    }

    private void CameraMirror_Changed(object sender, RoutedEventArgs args)
    {
        CameraPreviewCanvas?.Invalidate();
    }

    private void PreviewStatusTimer_Tick(object? sender, object args)
    {
        if (_previewCamera?.Warning is { } warning)
        {
            CameraStatusText.Text = warning;
            CameraPreviewCanvas.Invalidate();
        }
    }

    private async Task StopCameraPreviewAsync()
    {
        _previewStatusTimer.Stop();
        var camera = _previewCamera;
        _previewCamera = null;
        if (camera is not null)
        {
            camera.FrameAvailable -= PreviewCamera_FrameAvailable;
            try { await camera.DisposeAsync(); }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Camera preview cleanup: {exception.Message}"); }
        }
        CameraPreviewPlaceholder.Visibility = Visibility.Visible;
        TestCameraButton.Content = "Tes kamera";
        CameraPreviewCanvas.Invalidate();
    }

    private void SettingsDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (CameraEnabledToggle.IsOn && CameraDeviceComboBox.SelectedItem is not CameraDevice)
        {
            args.Cancel = true;
            SettingsErrorText.Text = "Pilih kamera yang terhubung, atau nonaktifkan kamera sebelum menyimpan.";
            SettingsErrorText.Visibility = Visibility.Visible;
        }
    }

    private async void SettingsDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        _closing = true;
        _previewCancellation.Cancel();
        var deferral = args.GetDeferral();
        try
        {
            await _previewGate.WaitAsync();
            try { await StopCameraPreviewAsync(); }
            finally { _previewGate.Release(); }
        }
        finally { deferral.Complete(); }
    }

    private void SettingsDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _previewCancellation.Dispose();
        _previewStatusTimer.Tick -= PreviewStatusTimer_Tick;
        CameraPreviewCanvas.RemoveFromVisualTree();
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StorageFolder folder;
            if (string.IsNullOrWhiteSpace(_folderPath))
            {
                var videos = await StorageFolder.GetFolderFromPathAsync(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
                folder = await videos.CreateFolderAsync(
                    "Clario Presenter",
                    CreationCollisionOption.OpenIfExists);
                _folderPath = folder.Path;
                UpdateFolderLabel();
            }
            else
            {
                folder = await StorageFolder.GetFolderFromPathAsync(_folderPath);
            }
            await Launcher.LaunchFolderAsync(folder);
        }
        catch
        {
            _folderPath = string.Empty;
            UpdateFolderLabel();
        }
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var owner = App.MainWindow is null
            ? nint.Zero
            : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var folder = await AppSettingsService.PickRecordingFolderAsync(owner);
        if (folder is null) return;
        _folderPath = folder.Path;
        UpdateFolderLabel();
    }

    private void UpdateFolderLabel()
    {
        FolderPathText.Text = string.IsNullOrWhiteSpace(_folderPath)
            ? "Videos\\Clario Presenter (folder bawaan)"
            : _folderPath;
    }
}
