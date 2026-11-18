using Clario_Presenter.Settings;
using Clario_Presenter.Camera;
using Clario_Presenter.Recording;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage;
using Windows.System;

namespace Clario_Presenter;

public sealed partial class SettingsDialog : ContentDialog
{
    private const string CameraDisabled = "Aktifkan kamera untuk melihat gambar langsung.";
    private const string TestCameraPrompt = "Tes kamera untuk melihat gambar langsung.";
    private const string CameraReady = "Pratinjau kamera aktif.";
    private static readonly string CameraWidthRange = $"{CameraOverlayLayout.MinWidthPercent}–{CameraOverlayLayout.MaxWidthPercent}%";
    private static readonly string CameraWidthError = $"Lebar kamera harus {CameraWidthRange} dari lebar video.";
    private string _folderPath;
    private readonly PresenterSettings _originalSettings;
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly CancellationTokenSource _previewCancellation = new();
    private readonly DispatcherTimer _previewStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CameraCaptureService? _previewCamera;
    private CameraOverlayOptions _previewLayout = new();
    private int? _customCameraWidthPercent;
    private int _previewUpdateQueued;
    private bool _refreshingCameras;
    private bool _closing;
    private bool _opened;

    public bool IsSaved { get; private set; }
    private bool UsesCustomCameraWidth => ReferenceEquals(CameraSizeComboBox.SelectedItem, CameraCustomSizeItem);

    public SettingsDialog(PresenterSettings settings, XamlRoot xamlRoot)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        UpdateDialogLayout();
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
        var widthPercent = Math.Clamp(settings.CameraWidthPercent, CameraOverlayLayout.MinWidthPercent, CameraOverlayLayout.MaxWidthPercent);
        CameraSizeComboBox.SelectedItem = CameraSizeComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, widthPercent.ToString())) ?? CameraCustomSizeItem;
        CameraWidthSlider.Minimum = CameraOverlayLayout.MinWidthPercent;
        CameraWidthSlider.Maximum = CameraOverlayLayout.MaxWidthPercent;
        if (UsesCustomCameraWidth) ShowCustomCameraWidth(widthPercent);
        CameraMirrorCheckBox.IsChecked = settings.CameraMirror;
        _previewStatusTimer.Tick += PreviewStatusTimer_Tick;
        UpdateFolderLabel();
        InitializeAudioSettings(settings);
        UpdateCameraLayout();
        UpdateCameraActions();
        CameraStatusText.Text = settings.CameraEnabled ? TestCameraPrompt : CameraDisabled;
        SettingsNavigation.SelectionChanged += SettingsNavigation_SelectionChanged;
        SettingsNavigation.SelectedItem = CameraNavigationItem;
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
            CameraPosition = (int)_previewLayout.Position,
            CameraWidthPercent = _previewLayout.WidthPercent,
            CameraMirror = _previewLayout.Mirror,
            ComputerAudioEnabled = ComputerAudioToggle.IsOn,
            PlaybackDeviceId = SelectedAudioDeviceId(PlaybackAudioComboBox, _originalSettings.PlaybackDeviceId),
            MicrophoneEnabled = MicrophoneAudioToggle.IsOn,
            MicrophoneDeviceId = SelectedAudioDeviceId(MicrophoneAudioComboBox, _originalSettings.MicrophoneDeviceId)
        };
    }

    private async void SettingsDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _opened = true;
        XamlRoot.Changed += SettingsRoot_Changed;
        UpdateDialogLayout();
        await Task.WhenAll(CameraEnabledToggle.IsOn ? RefreshCamerasAsync() : Task.CompletedTask,
            AudioEnabled ? RefreshAudioDevicesAsync() : Task.CompletedTask);
    }

    private void SettingsRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateDialogLayout();

    private void UpdateDialogLayout()
    {
        var available = XamlRoot.Size;
        var width = Math.Clamp(available.Width - 44, 1, 1000);
        var sideNavigation = width >= 780;
        var sideBySideCamera = sideNavigation ? width - SettingsNavigation.OpenPaneLength >= 618 : width >= 464;
        SettingsLayout.Width = width;
        SettingsLayout.Height = Math.Clamp(available.Height - 42, 1, 570);
        var paneMode = sideNavigation ? NavigationViewPaneDisplayMode.Left : NavigationViewPaneDisplayMode.Top;
        if (SettingsNavigation.PaneDisplayMode != paneMode)
        {
            SettingsNavigation.PaneDisplayMode = paneMode;
            CameraNavigationItem.Icon = sideNavigation ? CameraNavigationIcon : null;
            AudioNavigationItem.Icon = sideNavigation ? AudioNavigationIcon : null;
            VideoNavigationItem.Icon = sideNavigation ? VideoNavigationIcon : null;
            StorageNavigationItem.Icon = sideNavigation ? StorageNavigationIcon : null;
        }
        CameraControlsColumn.Width = sideBySideCamera
            ? sideNavigation ? new GridLength(0.65, GridUnitType.Star) : new GridLength(186)
            : new GridLength(0);
        CameraPreviewPane.MaxWidth = sideBySideCamera ? 442 : 350;
        CameraLayout.ColumnSpacing = sideBySideCamera ? 18 : 0;
        CameraLayout.RowSpacing = sideBySideCamera ? 0 : 16;
        Grid.SetColumn(CameraControls, sideBySideCamera ? 1 : 0);
        Grid.SetRow(CameraControls, sideBySideCamera ? 0 : 1);
    }

    private async void SettingsNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        CameraSection.Visibility = ReferenceEquals(args.SelectedItem, CameraNavigationItem) ? Visibility.Visible : Visibility.Collapsed;
        AudioSection.Visibility = ReferenceEquals(args.SelectedItem, AudioNavigationItem) ? Visibility.Visible : Visibility.Collapsed;
        VideoSection.Visibility = ReferenceEquals(args.SelectedItem, VideoNavigationItem) ? Visibility.Visible : Visibility.Collapsed;
        StorageSection.Visibility = ReferenceEquals(args.SelectedItem, StorageNavigationItem) ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.ChangeView(null, 0, null, disableAnimation: true);
        if (_opened && CameraSection.Visibility == Visibility.Collapsed) await StopHiddenCameraPreviewAsync();
    }

    private async void CameraEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_opened || _closing) return;
        UpdateCameraActions();
        SettingsErrorText.Visibility = Visibility.Collapsed;
        if (CameraEnabledToggle.IsOn)
        {
            if (CameraDeviceComboBox.Items.Count == 0) await RefreshCamerasAsync();
            else CameraStatusText.Text = CameraSelectionStatus();
            return;
        }

        CameraStatusText.Text = CameraDisabled;
        await StopHiddenCameraPreviewAsync();
    }

    private async Task StopHiddenCameraPreviewAsync()
    {
        if (_previewCamera is null || _closing) return;
        await _previewGate.WaitAsync();
        try
        {
            if (!_closing && (!CameraEnabledToggle.IsOn || CameraSection.Visibility == Visibility.Collapsed))
            {
                await StopCameraPreviewAsync();
                CameraStatusText.Text = CameraEnabledToggle.IsOn ? CameraSelectionStatus() : CameraDisabled;
                UpdateCameraActions();
            }
        }
        finally { _previewGate.Release(); }
    }

    private async void RefreshCameras_Click(object sender, RoutedEventArgs e) => await RefreshCamerasAsync();

    private async Task RefreshCamerasAsync()
    {
        if (_closing || !CameraEnabledToggle.IsOn || _refreshingCameras || _previewCamera is not null) return;
        _refreshingCameras = true;
        var selectedId = (CameraDeviceComboBox.SelectedItem as CameraDevice)?.Id ?? _originalSettings.CameraDeviceId;
        UpdateCameraActions();
        CameraStatusText.Text = "Memuat daftar kamera…";
        try
        {
            var devices = await CameraCaptureService.GetDevicesAsync();
            if (_closing) return;
            CameraDeviceComboBox.ItemsSource = devices;
            CameraDeviceComboBox.SelectedItem = string.IsNullOrEmpty(selectedId)
                ? devices.FirstOrDefault()
                : devices.FirstOrDefault(device => device.Id == selectedId);
            if (CameraEnabledToggle.IsOn) CameraStatusText.Text = CameraSelectionStatus();
        }
        catch (Exception exception)
        {
            if (!_closing && CameraEnabledToggle.IsOn) CameraStatusText.Text = $"Daftar kamera gagal dimuat: {exception.Message}";
        }
        finally
        {
            _refreshingCameras = false;
            if (!_closing) UpdateCameraActions();
        }
    }

    private string CameraSelectionStatus() => CameraDeviceComboBox.Items.Count == 0
        ? "Kamera belum ditemukan. Hubungkan webcam, lalu perbarui daftar."
        : CameraDeviceComboBox.SelectedItem is null
            ? "Kamera tersimpan belum terhubung. Pilih kamera yang tersedia."
            : TestCameraPrompt;

    private void CameraDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_opened || _closing || _refreshingCameras) return;
        UpdateCameraActions();
        SettingsErrorText.Visibility = Visibility.Collapsed;
        if (CameraDeviceComboBox.SelectedItem is CameraDevice) CameraStatusText.Text = TestCameraPrompt;
    }

    private void UpdateCameraActions()
    {
        var enabled = CameraEnabledToggle.IsOn && !_refreshingCameras;
        CameraDeviceComboBox.IsEnabled = enabled && _previewCamera is null;
        RefreshCamerasButton.IsEnabled = CameraDeviceComboBox.IsEnabled;
        TestCameraButton.IsEnabled = enabled &&
            (_previewCamera is not null || CameraDeviceComboBox.SelectedItem is CameraDevice);
    }

    private async void TestCamera_Click(object sender, RoutedEventArgs e)
    {
        await _previewGate.WaitAsync();
        try
        {
            if (_closing || !CameraEnabledToggle.IsOn || CameraSection.Visibility != Visibility.Visible) return;
            TestCameraButton.IsEnabled = false;
            CameraDeviceComboBox.IsEnabled = false;
            RefreshCamerasButton.IsEnabled = false;
            if (_previewCamera is not null)
            {
                await StopCameraPreviewAsync();
                CameraStatusText.Text = TestCameraPrompt;
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
            CameraPreviewCanvas.Visibility = Visibility.Visible;
            TestCameraButton.Content = "Hentikan tes";
            CameraStatusText.Text = CameraReady;
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
            if (!_closing) UpdateCameraActions();
            _previewGate.Release();
        }
    }

    private void PreviewCamera_FrameAvailable(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _previewUpdateQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(InvalidateCameraPreview))
            Interlocked.Exchange(ref _previewUpdateQueued, 0);
    }

    private void InvalidateCameraPreview()
    {
        Interlocked.Exchange(ref _previewUpdateQueued, 0);
        if (!_closing && _previewCamera is not null) CameraPreviewCanvas.Invalidate();
    }

    private void CameraPreviewCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var bounds = CameraOverlayLayout.GetBounds(sender.ActualWidth, sender.ActualHeight, _previewLayout);
        CameraOverlayRenderer.Draw(args.DrawingSession, _previewCamera, bounds, _previewLayout.Mirror);
    }

    private void CameraLayout_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_opened) UpdateCameraLayout();
    }

    private void CameraSizePreset_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_opened || _closing) return;

        if (UsesCustomCameraWidth) ShowCustomCameraWidth(_customCameraWidthPercent ?? _previewLayout.WidthPercent);
        else CustomCameraWidthControls.Visibility = Visibility.Collapsed;

        SettingsErrorText.Visibility = Visibility.Collapsed;
        UpdateCameraLayout();
    }

    private void ShowCustomCameraWidth(int width)
    {
        _customCameraWidthPercent = width;
        CameraWidthInput.Text = width.ToString();
        CameraWidthSlider.Value = width;
        CameraWidthHint.Text = $"{CameraWidthRange} dari lebar video";
        CustomCameraWidthControls.Visibility = Visibility.Visible;
    }

    private bool TryReadCustomCameraWidth(out int width) =>
        int.TryParse(CameraWidthInput.Text, out width)
        && width is >= CameraOverlayLayout.MinWidthPercent and <= CameraOverlayLayout.MaxWidthPercent;

    private void CameraWidthSlider_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_opened || _closing || !UsesCustomCameraWidth) return;
        var width = (int)args.NewValue;
        if (!TryReadCustomCameraWidth(out var typedWidth) || typedWidth != width)
            CameraWidthInput.Text = width.ToString();
    }

    private void CameraWidthInput_Changed(object sender, TextChangedEventArgs args)
    {
        if (!_opened || _closing || !UsesCustomCameraWidth) return;
        if (!TryReadCustomCameraWidth(out var width))
        {
            CameraWidthHint.Text = CameraWidthError;
            return;
        }

        _customCameraWidthPercent = width;
        CameraWidthSlider.Value = width;
        CameraWidthHint.Text = $"{CameraWidthRange} dari lebar video";
        SettingsErrorText.Visibility = Visibility.Collapsed;
        UpdateCameraLayout();
    }

    private void CameraMirror_Changed(object sender, RoutedEventArgs args)
    {
        if (_opened) UpdateCameraLayout();
    }

    private void UpdateCameraLayout()
    {
        var width = UsesCustomCameraWidth
            ? _customCameraWidthPercent ?? 24
            : int.TryParse((CameraSizeComboBox.SelectedItem as ComboBoxItem)?.Tag as string, out var presetWidth) ? presetWidth : 24;
        _previewLayout = new CameraOverlayOptions
        {
            Position = (CameraOverlayPosition)Math.Clamp(CameraPositionComboBox.SelectedIndex, 0, 3),
            WidthPercent = width,
            Mirror = CameraMirrorCheckBox.IsChecked == true
        };
        var bounds = CameraOverlayLayout.GetBounds(OutputRecordingService.OutputWidth, OutputRecordingService.OutputHeight, _previewLayout);
        CameraPreviewDetails.Text = $"{_previewLayout.WidthPercent}% lebar video · {bounds.Width:0} × {bounds.Height:0} px di 1080p";
        var previewBounds = CameraOverlayLayout.GetBounds(CameraPreviewFrame.Width, CameraPreviewFrame.Height, _previewLayout);
        Canvas.SetLeft(CameraPreviewPlaceholder, previewBounds.X);
        Canvas.SetTop(CameraPreviewPlaceholder, previewBounds.Y);
        CameraPreviewPlaceholder.Width = previewBounds.Width;
        CameraPreviewPlaceholder.Height = previewBounds.Height;
        CameraPlaceholderLabel.Visibility = previewBounds.Width >= 62 ? Visibility.Visible : Visibility.Collapsed;
        CameraPlaceholderIcon.FontSize = Math.Clamp(previewBounds.Height - 5, 7, 12);
        CameraPreviewCanvas.Invalidate();
    }

    private void PreviewStatusTimer_Tick(object? sender, object args)
    {
        var status = _previewCamera?.Warning ?? CameraReady;
        if (CameraStatusText.Text == status) return;
        CameraStatusText.Text = status;
        CameraPreviewCanvas.Invalidate();
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
        TestCameraButton.Content = "Tes kamera";
        CameraPreviewCanvas.Visibility = Visibility.Collapsed;
        CameraPreviewPlaceholder.Visibility = Visibility.Visible;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs args)
    {
        var error = GetAudioSelectionError();
        var section = AudioNavigationItem;
        var invalidCustomWidth = UsesCustomCameraWidth && !TryReadCustomCameraWidth(out _);
        if (error is null && invalidCustomWidth)
        {
            section = CameraNavigationItem;
            error = CameraWidthError;
        }
        else if (error is null && CameraEnabledToggle.IsOn && CameraDeviceComboBox.SelectedItem is not CameraDevice)
        {
            section = CameraNavigationItem;
            error = _refreshingCameras ? "Daftar kamera sedang dimuat." : "Pilih kamera yang terhubung untuk melanjutkan.";
        }
        if (error is null)
        {
            IsSaved = true;
            Hide();
            return;
        }

        SettingsNavigation.SelectedItem = section;
        SettingsErrorText.Text = error;
        SettingsErrorText.Visibility = Visibility.Visible;
        if (invalidCustomWidth && section == CameraNavigationItem) CameraWidthInput.Focus(FocusState.Programmatic);
    }

    private void Cancel_Click(object sender, RoutedEventArgs args) => Hide();

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
        XamlRoot.Changed -= SettingsRoot_Changed;
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
