using Clario_Presenter.Capture;
using Clario_Presenter.Camera;
using Clario_Presenter.Recording;
using Clario_Presenter.Services;
using Clario_Presenter.Settings;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using Windows.Storage;

namespace Clario_Presenter;

public sealed partial class MainPage : Page
{
    private readonly ObservableCollection<WindowSource> _sources = [];
    private readonly ObservableCollection<DisplayTarget> _displays = [];
    private readonly HashSet<nint> _privateWindows = [];
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _privacyWatchTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _windowRescueTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly AppSettingsService _settingsService = new();
    private TimeSpan _elapsed;
    private bool _isLive;
    private bool _isFrozen;
    private bool _isPrivacy;
    private bool _isAutoHeld;
    private bool _isSafeMirrorSession;
    private bool _isRecordingOnlySession;
    private bool _isProtectedPublicCapture;
    private long _lastPrivateViewTick;
    private nint _lastPublicWindow;
    private DisplayTarget? _clientDisplay;
    private DisplayTarget? _presenterDisplay;
    private CaptureScaleMode _outputScaleMode = CaptureScaleMode.Fit;
    private float _outputAdaptiveZoom = 1.15f;
    private CaptureSessionService? _capture;
    private OutputWindow? _outputWindow;
    private OutputRecordingService? _recorder;
    private StorageFile? _recordingFile;
    private bool _recordingFileWasAutoCreated;
    private DateTimeOffset _recordingStartedAt;
    private bool _recordingTransition;
    private string? _lastCameraWarning;

    private SolidColorBrush LiveBrush => ResourceBrush("ClarioLiveBrush");
    private SolidColorBrush FreezeBrush => ResourceBrush("ClarioFreezeBrush");
    private SolidColorBrush PrivacyBrush => ResourceBrush("ClarioPrivacyBrush");
    private SolidColorBrush MutedBrush => ResourceBrush("ClarioSecondaryTextBrush");

    public MainPage()
    {
        InitializeComponent();
        SourceComboBox.ItemsSource = _sources;
        DisplayComboBox.ItemsSource = _displays;
        SourceMonitorComboBox.ItemsSource = _displays;
        _sessionTimer.Tick += SessionTimer_Tick;
        _privacyWatchTimer.Tick += PrivacyWatchTimer_Tick;
        _windowRescueTimer.Tick += WindowRescueTimer_Tick;
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshSources();
        RefreshDisplays();
        UpdateCaptureModeUi();
        FreezeButton.IsEnabled = false;
        PrivacyButton.IsEnabled = false;
        UpdatePresentationState();
    }

    private void RefreshSources()
    {
        var selectedHandle = (SourceComboBox.SelectedItem as WindowSource)?.Handle;
        _sources.Clear();

        foreach (var source in WindowCatalogService.GetPresentableWindows())
        {
            _sources.Add(source);
        }

        SourceComboBox.SelectedItem = _sources.FirstOrDefault(item => item.Handle == selectedHandle)
                                      ?? _sources.FirstOrDefault();
        FooterStatusText.Text = _sources.Count == 0
            ? "Tidak ada jendela yang dapat dipresentasikan"
            : $"{_sources.Count} jendela tersedia · catatan tetap privat";
    }

    private void RefreshDisplays()
    {
        var selectedDeviceName = (DisplayComboBox.SelectedItem as DisplayTarget)?.DeviceName;
        var sourceDeviceName = (SourceMonitorComboBox.SelectedItem as DisplayTarget)?.DeviceName;
        _displays.Clear();
        foreach (var display in DisplayCatalogService.GetDisplays()) _displays.Add(display);
        DisplayComboBox.SelectedItem = _displays.FirstOrDefault(display =>
                                           display.DeviceName.Equals(selectedDeviceName, StringComparison.OrdinalIgnoreCase))
                                       ?? _displays.FirstOrDefault(display => !display.IsPrimary)
                                       ?? _displays.FirstOrDefault();
        SourceMonitorComboBox.SelectedItem = _displays.FirstOrDefault(display =>
            display.DeviceName.Equals(sourceDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? _displays.FirstOrDefault(display => display.IsPrimary) ?? _displays.FirstOrDefault();
    }

    private void DisplayComboBox_DropDownOpened(object sender, object e)
    {
        if (!_isLive) RefreshDisplays();
    }

    private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceComboBox.SelectedItem is not WindowSource source)
        {
            ActiveSourceTitle.Text = "Pilih jendela untuk mulai";
            ActiveSourceSubtitle.Text = "Hanya jendela terpilih yang akan dilihat klien";
            return;
        }

        ActiveSourceTitle.Text = source.Title;
        ActiveSourceSubtitle.Text = $"{source.ProcessName} · jendela ini menjadi satu-satunya sumber output";
        BrowserAddressText.Text = source.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase)
            || source.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase)
            ? "Jendela browser terpilih · address bar dapat dipotong"
            : $"Aplikasi desktop · {source.ProcessName}";
        DemoHeadingText.Text = source.ShortTitle;
        MiniOutputTitle.Text = source.ShortTitle;
    }

    private void RefreshSources_Click(object sender, RoutedEventArgs e) => RefreshSources();

    private bool IsSafeMirrorMode => CaptureModeComboBox.SelectedIndex == 1;
    private bool IsRecordingOnly => RecordingOnlyCheckBox.IsChecked == true;

    private void RecordingOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (SourceMonitorComboBox is not null && PrivateAppsButton is not null) UpdateCaptureModeUi();
    }

    private void SourceMonitorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLive && SourceMonitorComboBox.SelectedItem is DisplayTarget source
            && DisplayComboBox?.SelectedItem is DisplayTarget target && source.Handle == target.Handle)
            DisplayComboBox.SelectedItem = _displays.FirstOrDefault(display => display.Handle != source.Handle);
        if (PrivateAppsButton is not null) UpdateCaptureModeUi();
    }

    private void CaptureModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceMonitorComboBox is null || PrivateAppsButton is null || RecordingOnlyCheckBox is null) return;
        UpdateCaptureModeUi();
    }

    private void UpdateCaptureModeUi()
    {
        var safeMirror = IsSafeMirrorMode;
        SourceComboBox.IsEnabled = !safeMirror && !_isLive;
        SourceComboBox.Visibility = safeMirror ? Visibility.Collapsed : Visibility.Visible;
        SourceMonitorComboBox.Visibility = safeMirror ? Visibility.Visible : Visibility.Collapsed;
        SourceMonitorComboBox.IsEnabled = !_isLive;
        PrivateAppsButton.IsEnabled = safeMirror && !_isLive;
        RecordingOnlyCheckBox.IsEnabled = !_isLive;
        DisplayComboBox.IsEnabled = !IsRecordingOnly && !_isLive;
        OutputScaleComboBox.IsEnabled = !safeMirror && !_isLive;
        if (safeMirror) OutputScaleComboBox.SelectedIndex = 1;
        SourcePrivacyBadgeText.Text = safeMirror ? "MIRROR AMAN" : "JENDELA PRIVAT";
        ClientPreviewLabel.Text = IsRecordingOnly ? "Preview rekaman" : "Yang dilihat klien";

        if (safeMirror)
        {
            var monitor = SourceMonitorComboBox.SelectedItem as DisplayTarget;
            ActiveSourceTitle.Text = $"{(monitor is null ? "Monitor sumber" : $"Display {monitor.Index}")} · Mirror Aman";
            ActiveSourceSubtitle.Text = "Semua aktivitas tampil, kecuali Clario dan aplikasi privat";
            BrowserAddressText.Text = "Desktop utuh · tanpa crop · taskbar tetap tampil";
            DemoHeadingText.Text = "Mirror Aman";
            MiniOutputTitle.Text = "Monitor sumber";
        }
        else
        {
            SourceComboBox_SelectionChanged(SourceComboBox, null!);
        }

        UpdatePresentationState();
    }

    private void PrivateAppsButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        var applications = WindowCatalogService.GetPresentableWindows()
            .OrderBy(application => application.ProcessName)
            .ThenBy(application => application.Title)
            .ToArray();

        if (applications.Length == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = "Tidak ada aplikasi tersedia", IsEnabled = false });
        }

        foreach (var application in applications)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = $"{application.Title}  ·  {application.ProcessName}",
                Tag = application.Handle.ToInt64(),
                IsChecked = _privateWindows.Contains(application.Handle)
            };
            item.Click += PrivateAppItem_Click;
            flyout.Items.Add(item);
        }

        flyout.ShowAt(PrivateAppsButton);
    }

    private void PrivateAppItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item || item.Tag is not long rawHandle) return;
        var windowHandle = (nint)rawHandle;
        if (item.IsChecked) _privateWindows.Add(windowHandle);
        else _privateWindows.Remove(windowHandle);
        PrivateAppsButtonText.Text = $"Privat {_privateWindows.Count}";
    }

    private async void LiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isLive && ((!IsRecordingOnly && DisplayComboBox.SelectedItem is null)
            || (IsSafeMirrorMode && SourceMonitorComboBox.SelectedItem is null)
            || (!IsSafeMirrorMode && SourceComboBox.SelectedItem is null)))
        {
            FooterStatusText.Text = "Pilih jendela sumber dan monitor output terlebih dahulu";
            return;
        }

        if (!_isLive)
        {
            var source = SourceComboBox.SelectedItem as WindowSource;
            var target = IsRecordingOnly ? null : DisplayComboBox.SelectedItem as DisplayTarget;
            var monitor = SourceMonitorComboBox.SelectedItem as DisplayTarget;
            if ((!IsRecordingOnly && target is null)
                || (IsSafeMirrorMode && monitor is null) || (!IsSafeMirrorMode && source is null))
            {
                FooterStatusText.Text = "Pilih jendela sumber dan monitor output terlebih dahulu";
                return;
            }

            try
            {
                StartPresentation(source, monitor, target);
            }
            catch (Exception exception)
            {
                StopPresentation();
                FooterStatusText.Text = $"Capture gagal · {exception.Message}";
                SetWindowStatus("Capture gagal", PrivacyBrush);
                return;
            }

            _isLive = true;
            _elapsed = TimeSpan.Zero;
            _sessionTimer.Start();
            CaptureModeComboBox.IsEnabled = false;
            SourceComboBox.IsEnabled = false;
            DisplayComboBox.IsEnabled = false;
            OutputScaleComboBox.IsEnabled = false;
            SourceMonitorComboBox.IsEnabled = false;
            RecordingOnlyCheckBox.IsEnabled = false;
            PrivateAppsButton.IsEnabled = false;
        }
        else
        {
            await StopRecordingAsync();
            StopPresentation();
        }

        FreezeButton.IsEnabled = _isLive;
        PrivacyButton.IsEnabled = _isLive;
        RecordButton.IsEnabled = _isLive;
        if (!_isLive)
        {
            CaptureModeComboBox.IsEnabled = true;
            DisplayComboBox.IsEnabled = true;
            OutputScaleComboBox.IsEnabled = true;
            UpdateCaptureModeUi();
        }
        UpdatePresentationState();
    }

    private void FreezeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isLive) return;
        _isFrozen = FreezeButton.IsChecked == true;
        if (_capture is not null) _capture.IsFrozen = _isFrozen;
        if (_isFrozen)
        {
            _isPrivacy = false;
            PrivacyButton.IsChecked = false;
        }
        UpdatePresentationState();
    }

    private void PrivacyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isLive) return;
        _isPrivacy = PrivacyButton.IsChecked == true;
        if (_isPrivacy)
        {
            _isFrozen = false;
            FreezeButton.IsChecked = false;
            if (_capture is not null) _capture.IsFrozen = false;
        }
        UpdatePresentationState();
    }

    private void NotesButton_Click(object sender, RoutedEventArgs e) => SetNotesVisible(NotesButton.IsChecked == true);

    private void CollapseNotes_Click(object sender, RoutedEventArgs e)
    {
        NotesButton.IsChecked = false;
        SetNotesVisible(false);
    }

    private void SetNotesVisible(bool visible)
    {
        NotesPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        NotesColumn.Width = visible ? new GridLength(320) : new GridLength(0);
        StudioGrid.ColumnSpacing = visible ? 12 : 0;
    }

    private void SessionTimer_Tick(object? sender, object e)
    {
        _elapsed = _elapsed.Add(TimeSpan.FromSeconds(1));
        TimerText.Text = _elapsed.ToString(@"mm\:ss");
        if (_recorder?.IsRecording == true)
        {
            var recordingElapsed = DateTimeOffset.Now - _recordingStartedAt;
            RecordButtonText.Text = $"REC {recordingElapsed:mm\\:ss}";
            var cameraWarning = _recorder.CameraWarning;
            if (cameraWarning != _lastCameraWarning && cameraWarning is not null)
                FooterStatusText.Text = $"{cameraWarning} · rekaman layar tetap berjalan";
            _lastCameraWarning = cameraWarning;
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_settingsService.Current)
        {
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        _settingsService.Save(dialog.GetSettings());
        var settings = _settingsService.Current;
        FooterStatusText.Text = $"Settings tersimpan · 1080p {settings.RecordingFrameRate} fps · {settings.RecordingBitrateMbps} Mbps"
            + (settings.CameraEnabled ? " · kamera aktif untuk rekaman berikutnya" : string.Empty);
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingTransition)
        {
            RecordButton.IsChecked = _recorder?.IsRecording == true;
            return;
        }

        _recordingTransition = true;
        RecordButton.IsEnabled = false;
        LiveButton.IsEnabled = false;
        SettingsButton.IsEnabled = false;
        StorageFile? pendingFile = null;
        var pendingFileWasAutoCreated = false;
        try
        {
            if (_recorder?.IsRecording == true)
            {
                await StopRecordingAsync();
                return;
            }

            if (!_isLive || _capture is null)
            {
                RecordButton.IsChecked = false;
                FooterStatusText.Text = "Mulai Live sebelum merekam";
                return;
            }

            var settings = _settingsService.Current;
            pendingFileWasAutoCreated = !settings.AskRecordingLocation;
            var file = settings.AskRecordingLocation
                ? await AppSettingsService.PickRecordingFileAsync(GetPresenterWindowHandle())
                : await _settingsService.CreateRecordingFileAsync();
            if (file is null)
            {
                RecordButton.IsChecked = false;
                return;
            }
            pendingFile = file;

            if (!_isLive || _capture is null)
                throw new InvalidOperationException("Sesi Live telah berhenti. Mulai Live kembali sebelum merekam.");

            var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var recorder = new OutputRecordingService();
            try
            {
                await recorder.StartAsync(
                    _capture,
                    _outputScaleMode,
                    _outputAdaptiveZoom,
                    ResolveClientOutputMode,
                    stream,
                    new RecordingOptions(settings.RecordingFrameRate, settings.RecordingBitrateMbps,
                        new CameraOverlayOptions
                        {
                            Enabled = settings.CameraEnabled,
                            DeviceId = settings.CameraDeviceId,
                            Position = (CameraOverlayPosition)settings.CameraPosition,
                            WidthPercent = settings.CameraWidthPercent,
                            Mirror = settings.CameraMirror
                        }));
                if (!_isLive || _capture is null)
                    throw new InvalidOperationException("Sesi Live berhenti saat kamera disiapkan.");
            }
            catch
            {
                await recorder.DisposeAsync();
                throw;
            }

            _recorder = recorder;
            _recordingFile = file;
            _recordingFileWasAutoCreated = pendingFileWasAutoCreated;
            _recordingStartedAt = DateTimeOffset.Now;
            _lastCameraWarning = null;
            RecordButton.IsChecked = true;
            RecordButtonText.Text = "REC 00:00";
            RecordButtonDot.Fill = PrivacyBrush;
            FooterStatusText.Text = $"Merekam {(recorder.IncludesCamera ? "layar + kamera" : "output klien")} · 1080p {settings.RecordingFrameRate} fps · H.264";
            _ = WatchRecordingAsync(recorder);
        }
        catch (Exception exception)
        {
            if (pendingFileWasAutoCreated) await DeleteEmptyRecordingAsync(pendingFile);
            RecordButton.IsChecked = false;
            RecordButtonText.Text = "Rekam";
            FooterStatusText.Text = $"Rekaman gagal dimulai · {exception.Message}";
        }
        finally
        {
            _recordingTransition = false;
            RecordButton.IsEnabled = _isLive;
            LiveButton.IsEnabled = true;
            SettingsButton.IsEnabled = true;
        }
    }

    private async Task StopRecordingAsync()
    {
        var recorder = _recorder;
        var file = _recordingFile;
        _recorder = null;
        _recordingFile = null;
        _recordingFileWasAutoCreated = false;
        RecordButton.IsEnabled = false;
        RecordButtonText.Text = "Menyimpan…";

        if (recorder is not null)
        {
            try
            {
                await recorder.StopAsync();
                FooterStatusText.Text = file is null
                    ? "Rekaman selesai"
                    : $"Rekaman tersimpan · {file.Name}";
            }
            catch (Exception exception)
            {
                FooterStatusText.Text = $"Finalisasi rekaman gagal · {exception.Message}";
            }
            finally
            {
                await recorder.DisposeAsync();
            }
        }

        RecordButton.IsChecked = false;
        RecordButtonText.Text = "Rekam";
        RecordButton.IsEnabled = _isLive;
    }

    private async Task WatchRecordingAsync(OutputRecordingService recorder)
    {
        Exception? failure = null;
        try
        {
            await recorder.Completion;
        }
        catch (Exception exception)
        {
            failure = recorder.Failure ?? exception;
        }

        // A normal StopRecordingAsync clears _recorder before signalling the
        // media pipeline. Only handle completion here when the encoder ended
        // on its own (for example after a driver/device failure).
        if (!ReferenceEquals(_recorder, recorder)) return;

        var file = _recordingFile;
        var fileWasAutoCreated = _recordingFileWasAutoCreated;
        _recorder = null;
        _recordingFile = null;
        _recordingFileWasAutoCreated = false;
        try
        {
            await recorder.DisposeAsync();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        if (fileWasAutoCreated) await DeleteEmptyRecordingAsync(file);
        RecordButton.IsChecked = false;
        RecordButtonText.Text = "Rekam";
        RecordButton.IsEnabled = _isLive;
        FooterStatusText.Text = failure is null
            ? "Rekaman berhenti lebih awal · silakan mulai ulang REC"
            : $"Encoder berhenti · {failure.Message}";
    }

    private static async Task DeleteEmptyRecordingAsync(StorageFile? file)
    {
        if (file is null) return;
        try
        {
            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size == 0) await file.DeleteAsync(StorageDeleteOption.Default);
        }
        catch
        {
            // Cleanup is best effort; never hide the original recording error.
        }
    }

    private void StartPresentation(WindowSource? source, DisplayTarget? sourceMonitor, DisplayTarget? target)
    {
        if (IsSafeMirrorMode && target is not null && sourceMonitor?.Handle == target.Handle)
            throw new InvalidOperationException("Monitor sumber dan output harus berbeda. Centang Rekam saja jika hanya ingin merekam monitor ini.");
        _capture = new CaptureSessionService();
        _capture.FrameAvailable += Capture_FrameAvailable;
        _capture.SourceClosed += Capture_SourceClosed;
        _capture.CaptureFailed += Capture_CaptureFailed;
        _isSafeMirrorSession = IsSafeMirrorMode;
        _isRecordingOnlySession = IsRecordingOnly;
        _presenterDisplay = sourceMonitor ?? _displays.FirstOrDefault(display => display.IsPrimary);
        _clientDisplay = target is not null && target.Handle != _presenterDisplay?.Handle ? target : null;
        _outputScaleMode = _isSafeMirrorSession ? CaptureScaleMode.Fit : ResolveOutputScaleMode();
        _outputAdaptiveZoom = target is null ? 1 : CalculateAdaptiveZoom(_presenterDisplay, target);

        if (_clientDisplay is not null)
        {
            WindowCatalogService.MoveWindowsToDisplay(_clientDisplay.Handle, _presenterDisplay!.Bounds);
            _windowRescueTimer.Start();
        }

        if (_isSafeMirrorSession)
        {
            var primaryDisplay = _presenterDisplay
                ?? throw new InvalidOperationException("Monitor sumber tidak ditemukan.");
            // Begin from a known-safe desktop. Selected private windows are still
            // available from the taskbar and will trigger auto-hold when restored.
            _isAutoHeld = true;
            WindowCatalogService.MinimizeWindows(_privateWindows);
            _lastPrivateViewTick = Environment.TickCount64;
            _capture.ConfigureDesktop(primaryDisplay.Bounds, primaryDisplay.WorkArea);
            _capture.FrameHoldPredicate = () => _isAutoHeld || _isProtectedPublicCapture || HasPrivateView();
            _capture.StartMonitor(primaryDisplay.Handle, keepLastFrame: true);
            _capture.SetProtectedWindows(WindowCatalogService.GetPublicDesktopWindows(primaryDisplay.WorkArea, _privateWindows));
            _privacyWatchTimer.Start();
        }
        else
        {
            _capture.Start(source!.Handle);
        }

        LivePreviewCanvas.Visibility = Visibility.Visible;
        if (target is not null)
        {
            var previewMode = target.IsPrimary && _displays.Count == 1;
            _outputWindow = new OutputWindow(_capture, target, previewMode, _outputScaleMode, _outputAdaptiveZoom);
            _outputWindow.Closed += OutputWindow_Closed;
            _outputWindow.Activate();
            _outputWindow.ApplyPlacement();
        }
        (App.MainWindow as MainWindow)?.Activate();
        if (_isSafeMirrorSession && App.MainWindow is MainWindow mirrorWindow)
        {
            mirrorWindow.AutoMinimizeOnDeactivate = true;
            mirrorWindow.MinimizeForPresentation();
        }
    }

    private void StopPresentation()
    {
        _sessionTimer.Stop();
        _privacyWatchTimer.Stop();
        _windowRescueTimer.Stop();
        if (App.MainWindow is MainWindow mirrorWindow) mirrorWindow.AutoMinimizeOnDeactivate = false;
        _isLive = false;
        _isFrozen = false;
        _isPrivacy = false;
        _isAutoHeld = false;
        _isSafeMirrorSession = false;
        _isRecordingOnlySession = false;
        _isProtectedPublicCapture = false;
        _lastPublicWindow = nint.Zero;
        _clientDisplay = null;
        _presenterDisplay = null;
        FreezeButton.IsChecked = false;
        PrivacyButton.IsChecked = false;

        if (_outputWindow is not null)
        {
            _outputWindow.Closed -= OutputWindow_Closed;
            _outputWindow.Close();
            _outputWindow = null;
        }

        if (_capture is not null)
        {
            _capture.FrameAvailable -= Capture_FrameAvailable;
            _capture.SourceClosed -= Capture_SourceClosed;
            _capture.CaptureFailed -= Capture_CaptureFailed;
            _capture.Dispose();
            _capture = null;
        }

        LivePreviewCanvas.Visibility = Visibility.Collapsed;
        FreezeButton.IsEnabled = false;
        PrivacyButton.IsEnabled = false;
        RecordButton.IsEnabled = false;
        CaptureModeComboBox.IsEnabled = true;
        DisplayComboBox.IsEnabled = true;
        OutputScaleComboBox.IsEnabled = true;
        UpdateCaptureModeUi();
    }

    private bool IsForegroundPrivate()
    {
        var foregroundWindow = WindowCatalogService.GetForegroundWindowHandle();
        var presenterWindow = GetPresenterWindowHandle();
        if (foregroundWindow == presenterWindow) return true;

        return WindowCatalogService.IsInWindowFamily(foregroundWindow, _privateWindows)
            && WindowCatalogService.IsWindowPresentable(foregroundWindow);
    }

    private bool HasPrivateView()
    {
        if (IsForegroundPrivate() || WindowCatalogService.IsApplicationSwitcherActive()
            || (_presenterDisplay is not null
                && WindowCatalogService.HasPrivateWindowOnDisplay(_presenterDisplay.Bounds, _privateWindows)))
        {
            _lastPrivateViewTick = Environment.TickCount64;
            return true;
        }
        // Do not capture the last few frames of a private window's minimize
        // animation after Windows has already marked its HWND as minimized.
        return Environment.TickCount64 - _lastPrivateViewTick < 300;
    }

    private void PrivacyWatchTimer_Tick(object? sender, object e)
    {
        if (!_isSafeMirrorSession || _isFrozen || _capture is null || _presenterDisplay is null) return;

        var foregroundWindow = WindowCatalogService.GetForegroundWindowHandle();
        var presenterActive = foregroundWindow == GetPresenterWindowHandle();
        var privateWindowActive = WindowCatalogService.IsInWindowFamily(foregroundWindow, _privateWindows)
            && WindowCatalogService.IsWindowPresentable(foregroundWindow);
        var protectedViewNeeded = HasPrivateView();
        var switcherActive = WindowCatalogService.IsApplicationSwitcherActive();

        if (!presenterActive && !privateWindowActive
            && !switcherActive && WindowCatalogService.IsWindowCapturable(foregroundWindow)
            && WindowCatalogService.TryGetCaptureBounds(foregroundWindow, out var foregroundBounds)
            && DesktopWindowPlan.Intersects(_presenterDisplay.Bounds, foregroundBounds))
        {
            _lastPublicWindow = foregroundWindow;
        }

        var wasProtected = _isProtectedPublicCapture;
        var wasHeld = _isAutoHeld;
        // Keep public captures prepared in BOTH states. Opening a private app
        // changes only whether the monitor backdrop is held, never the source
        // dimensions, window capture sessions, or the recorder's Live state.
        if (!switcherActive)
        {
            TryUpdateProtectedDesktop(protectedViewNeeded);
        }
        _isProtectedPublicCapture = protectedViewNeeded;
        _capture.IsDesktopProtected = protectedViewNeeded;
        _isAutoHeld = false;

        if (wasProtected != _isProtectedPublicCapture || wasHeld != _isAutoHeld)
            UpdatePresentationState();
    }

    private bool TryUpdateProtectedDesktop(bool protectedViewNeeded)
    {
        if (_capture is null || _presenterDisplay is null) return false;

        try
        {
            if (protectedViewNeeded && _lastPublicWindow != nint.Zero
                && !WindowCatalogService.IsInWindowFamily(_lastPublicWindow, _privateWindows))
                WindowCatalogService.PrepareWindowForBackgroundCapture(_lastPublicWindow);
            var publicWindows = WindowCatalogService.GetPublicDesktopWindows(_presenterDisplay.WorkArea, _privateWindows);
            return _capture.SetProtectedWindows(publicWindows, retainSceneIfEmpty: protectedViewNeeded);
        }
        catch
        {
            // Keep the last good composition on transient enumeration/driver
            // failures. Never tear down a working scene during a private detour.
            return false;
        }
    }

    private static nint GetPresenterWindowHandle() => App.MainWindow is null
        ? nint.Zero
        : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);

    private CaptureScaleMode ResolveOutputScaleMode() => OutputScaleComboBox.SelectedIndex switch
    {
        1 => CaptureScaleMode.Fit,
        2 => CaptureScaleMode.Fill,
        _ => CaptureScaleMode.Adaptive
    };

    private static float CalculateAdaptiveZoom(DisplayTarget? source, DisplayTarget target)
    {
        if (source is null || source.Bounds.Width <= 0 || source.Bounds.Height <= 0
            || target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
        {
            return 1.15f;
        }

        var sourceLogicalArea = source.Bounds.Width * 96d / Math.Max(96u, source.DpiX)
            * (source.Bounds.Height * 96d / Math.Max(96u, source.DpiY));
        var targetLogicalArea = target.Bounds.Width * 96d / Math.Max(96u, target.DpiX)
            * (target.Bounds.Height * 96d / Math.Max(96u, target.DpiY));
        var logicalAreaRatio = Math.Sqrt(sourceLogicalArea / targetLogicalArea);
        var physicalSizeRatio = source.EstimatedDiagonalInches is double sourceDiagonal
            && target.EstimatedDiagonalInches is double targetDiagonal
            && targetDiagonal > 0
                ? sourceDiagonal / targetDiagonal
                : 1d;

        // Raw DPI distinguishes a physically small laptop display from a large
        // external monitor even when both use the same Windows scale setting.
        // Keep the zoom conservative so useful edges are not cropped too much.
        var readabilityPressure = Math.Max(logicalAreaRatio, physicalSizeRatio);
        return (float)Math.Clamp(
            1 + Math.Max(0, readabilityPressure - 1) * 0.3,
            1,
            1.32);
    }

    private void WindowRescueTimer_Tick(object? sender, object e)
    {
        if (!_isLive || _clientDisplay is null || _presenterDisplay is null) return;
        WindowCatalogService.MoveWindowsToDisplay(_clientDisplay.Handle, _presenterDisplay.Bounds);
    }

    private void Capture_FrameAvailable(object? sender, EventArgs e)
    {
        if (_isLive) LivePreviewCanvas.Invalidate();
    }

    private void Capture_SourceClosed(object? sender, EventArgs e)
    {
        HandleSourceLost("Jendela sumber telah ditutup.");
    }

    private void Capture_CaptureFailed(object? sender, CaptureFailureEventArgs e)
    {
        var detail = $"{e.Message} {e.Exception.Message}";
        HandleSourceLost(detail);
    }

    private void HandleSourceLost(string detail)
    {
        if (!_isLive) return;
        _capture?.Stop();
        _isFrozen = false;
        _isPrivacy = true;
        FreezeButton.IsChecked = false;
        PrivacyButton.IsChecked = true;
        UpdatePresentationState();
        _outputWindow?.SetMode(ClientOutputMode.SourceLost, detail);
        FooterStatusText.Text = $"Sumber terputus · {detail}";
    }

    private async void OutputWindow_Closed(object sender, WindowEventArgs args)
    {
        _outputWindow = null;
        await StopRecordingAsync();
        StopPresentation();
        UpdatePresentationState();
    }

    private void LivePreviewCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args) => _capture?.Draw(sender, args);

    private async void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        await StopRecordingAsync();
        StopPresentation();
        LivePreviewCanvas.RemoveFromVisualTree();
    }

    private void UpdatePresentationState()
    {
        var state = ResolveState();
        var brush = state switch
        {
            PresentationState.Live => LiveBrush,
            PresentationState.Frozen => FreezeBrush,
            PresentationState.Privacy => PrivacyBrush,
            _ => MutedBrush
        };

        ClientStatusText.Foreground = brush;
        FooterStatusIcon.Foreground = brush;
        LiveButtonDot.Fill = _isLive ? PrivacyBrush : LiveBrush;
        LiveButtonText.Text = _isLive ? "Stop" : IsRecordingOnly ? "Siapkan rekaman" : "Mulai Live";

        switch (state)
        {
            case PresentationState.Live:
                _outputWindow?.SetMode(ClientOutputMode.Live);
                ClientStatusText.Text = _isRecordingOnlySession ? "SIAP REC" : _isProtectedPublicCapture ? "LIVE AMAN" : "LIVE";
                ClientStateOverlay.Visibility = Visibility.Collapsed;
                FooterStatusText.Text = _isProtectedPublicCapture
                    ? "Live Terlindungi · aplikasi publik terus berjalan saat berpindah ke aplikasi privat"
                    : _isRecordingOnlySession
                        ? "Layar siap direkam · tekan Rekam · monitor kedua tetap bebas digunakan"
                    : _isSafeMirrorSession
                        ? "Mirror Aman · seluruh desktop tanpa crop, taskbar tetap tampil"
                        : "Live · monitor klien hanya menerima jendela terpilih";
                SetWindowStatus(
                    _isRecordingOnlySession ? "Rekaman siap" : _isProtectedPublicCapture ? "Live terlindungi" : "Live ke monitor klien",
                    LiveBrush);
                break;
            case PresentationState.Frozen:
                var waitingForPublicApp = _isAutoHeld && !(_capture?.HasFrame ?? false);
                _outputWindow?.SetMode(
                    waitingForPublicApp ? ClientOutputMode.Privacy : ClientOutputMode.Frozen,
                    waitingForPublicApp ? "Aktifkan Canva, browser, atau aplikasi publik" : null);
                ClientStatusText.Text = "FROZEN";
                ClientStateOverlay.Visibility = Visibility.Visible;
                ClientStateIcon.Glyph = "\uE768";
                ClientStateTitle.Text = waitingForPublicApp
                    ? "Menunggu aplikasi publik"
                    : _isAutoHeld ? "Aplikasi privat aktif" : "Tampilan dibekukan";
                ClientStateSubtitle.Text = waitingForPublicApp
                    ? "Buka aplikasi yang akan dipresentasikan"
                    : "Klien melihat frame aman terakhir";
                FooterStatusText.Text = waitingForPublicApp
                    ? "Mirror Aman siap · pindah ke Canva atau web untuk mulai"
                    : _isAutoHeld ? "Auto-hold · aplikasi privat tidak diteruskan ke monitor klien"
                    : "Freeze aktif · Anda aman membuka PDF atau aplikasi lain";
                SetWindowStatus(_isAutoHeld ? "Aplikasi privat ditahan" : "Output dibekukan", FreezeBrush);
                break;
            case PresentationState.Privacy:
                _outputWindow?.SetMode(ClientOutputMode.Privacy);
                ClientStatusText.Text = "PRIVACY";
                ClientStateOverlay.Visibility = Visibility.Visible;
                ClientStateIcon.Glyph = "\uE890";
                ClientStateTitle.Text = "Privacy Mode";
                ClientStateSubtitle.Text = "Output klien disembunyikan";
                FooterStatusText.Text = "Privacy aktif · layar klien tidak menampilkan sumber";
                SetWindowStatus("Privacy aktif", PrivacyBrush);
                break;
            default:
                ClientStatusText.Text = "SIAP";
                ClientStateOverlay.Visibility = Visibility.Visible;
                ClientStateIcon.Glyph = "\uE930";
                ClientStateTitle.Text = "Belum Live";
                ClientStateSubtitle.Text = "Pilih sumber lalu mulai";
                FooterStatusText.Text = IsRecordingOnly
                    ? "Pilih monitor sumber dan aplikasi privat, tekan Siapkan rekaman lalu Rekam"
                    : IsSafeMirrorMode
                    ? "Siap · Mirror Aman menampilkan desktop utuh dan taskbar tanpa crop"
                    : "Siap · pilih sumber dan monitor output";
                SetWindowStatus("Siap", MutedBrush);
                break;
        }
    }

    private PresentationState ResolveState()
    {
        if (!_isLive) return PresentationState.Ready;
        if (_isPrivacy) return PresentationState.Privacy;
        if (_isFrozen || _isAutoHeld) return PresentationState.Frozen;
        return PresentationState.Live;
    }

    private ClientOutputMode ResolveClientOutputMode() => ResolveState() switch
    {
        PresentationState.Live => ClientOutputMode.Live,
        PresentationState.Frozen => ClientOutputMode.Frozen,
        PresentationState.Privacy => ClientOutputMode.Privacy,
        _ => ClientOutputMode.SourceLost
    };

    private void SetWindowStatus(string label, SolidColorBrush brush)
    {
        if (App.MainWindow is MainWindow window) window.SetSessionStatus(label, brush);
    }

    private static SolidColorBrush ResourceBrush(string key) => (SolidColorBrush)Application.Current.Resources[key];

    private enum PresentationState { Ready, Live, Frozen, Privacy }
}
