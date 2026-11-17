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
    private readonly ObservableCollection<DisplayTarget> _audienceDisplays = [];
    private readonly HashSet<nint> _privateWindows = [];
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _privacyWatchTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _windowRescueTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly AppSettingsService _settingsService = new();
    private bool _isLoaded;
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
    private bool _sessionTransition;
    private bool _refreshingDisplays;
    private string? _lastCameraWarning;

    private SolidColorBrush LiveBrush => ResourceBrush("ClarioLiveBrush");
    private SolidColorBrush FreezeBrush => ResourceBrush("ClarioFreezeBrush");
    private SolidColorBrush PrivacyBrush => ResourceBrush("ClarioPrivacyBrush");
    private SolidColorBrush MutedBrush => ResourceBrush("ClarioSecondaryTextBrush");

    public MainPage()
    {
        InitializeComponent();
        InitializeFloatingNotes();
        SourceComboBox.ItemsSource = _sources;
        DisplayComboBox.ItemsSource = _audienceDisplays;
        SourceMonitorComboBox.ItemsSource = _displays;
        _sessionTimer.Tick += SessionTimer_Tick;
        _privacyWatchTimer.Tick += PrivacyWatchTimer_Tick;
        _windowRescueTimer.Tick += WindowRescueTimer_Tick;
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        RefreshDisplays();
        UpdateCaptureModeUi();
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
    }

    private void RefreshDisplays()
    {
        var sourceDeviceName = (SourceMonitorComboBox.SelectedItem as DisplayTarget)?.DeviceName;
        _refreshingDisplays = true;
        try
        {
            _displays.Clear();
            foreach (var display in DisplayCatalogService.GetDisplays()) _displays.Add(display);
            SourceMonitorComboBox.SelectedItem = _displays.FirstOrDefault(display =>
                display.DeviceName.Equals(sourceDeviceName, StringComparison.OrdinalIgnoreCase))
                ?? _displays.FirstOrDefault(display => display.IsPrimary) ?? _displays.FirstOrDefault();
        }
        finally { _refreshingDisplays = false; }

        RefreshAudienceDisplays();
        UpdateSourceLabel();
    }

    private void RefreshAudienceDisplays()
    {
        var selectedDeviceName = (DisplayComboBox.SelectedItem as DisplayTarget)?.DeviceName;
        var source = IsSafeMirrorMode ? SourceMonitorComboBox.SelectedItem as DisplayTarget : null;
        _audienceDisplays.Clear();
        foreach (var display in _displays.Where(display => display.Handle != source?.Handle))
            _audienceDisplays.Add(display);
        DisplayComboBox.SelectedItem = _audienceDisplays.FirstOrDefault(display =>
                                           display.DeviceName.Equals(selectedDeviceName, StringComparison.OrdinalIgnoreCase))
                                       ?? _audienceDisplays.FirstOrDefault(display => !display.IsPrimary)
                                       ?? _audienceDisplays.FirstOrDefault();
    }

    private void DisplayComboBox_DropDownOpened(object sender, object e)
    {
        if (!_isLive) RefreshDisplays();
    }

    private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSourceLabel();

    private void SourceMonitorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingDisplays || _isLive || !_isLoaded) return;
        RefreshAudienceDisplays();
        UpdateSourceLabel();
    }

    private void UpdateSourceLabel()
    {
        if (ActiveSourceTitle is null) return;
        ActiveSourceTitle.Text = IsSafeMirrorMode
            ? (SourceMonitorComboBox.SelectedItem as DisplayTarget)?.ShortDisplayName ?? "Pilih layar"
            : (SourceComboBox.SelectedItem as WindowSource)?.Title ?? "Pilih aplikasi";
    }

    private void RefreshSources_Click(object sender, RoutedEventArgs e)
    {
        RefreshSources();
        if (_sources.Count == 0)
            FooterStatusText.Text = "Buka aplikasi yang ingin dipilih, lalu perbarui daftar.";
    }

    private bool IsSafeMirrorMode => CaptureModeComboBox.SelectedIndex == 1;
    private bool IsRecordingOnly => SessionModeComboBox.SelectedIndex == 0;

    private void SessionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoaded) UpdateCaptureModeUi();
    }

    private void CaptureModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        if (!IsSafeMirrorMode) RefreshSources();
        RefreshAudienceDisplays();
        UpdateCaptureModeUi();
    }

    private void UpdateCaptureModeUi()
    {
        var selecting = !_isLive;
        var screen = IsSafeMirrorMode;
        var presentation = !IsRecordingOnly;
        SessionModeComboBox.IsEnabled = selecting;
        CaptureModeComboBox.IsEnabled = selecting;
        SourceComboBox.Visibility = screen ? Visibility.Collapsed : Visibility.Visible;
        SourceComboBox.IsEnabled = selecting;
        SourceMonitorComboBox.Visibility = screen ? Visibility.Visible : Visibility.Collapsed;
        SourceMonitorComboBox.IsEnabled = selecting;
        RefreshSourcesButton.Visibility = screen ? Visibility.Collapsed : Visibility.Visible;
        RefreshSourcesButton.IsEnabled = selecting;
        PrivateAppsButton.Visibility = screen ? Visibility.Visible : Visibility.Collapsed;
        PrivateAppsButton.IsEnabled = selecting;
        OutputOptions.Visibility = presentation || !screen ? Visibility.Visible : Visibility.Collapsed;
        DisplayComboBox.Visibility = presentation ? Visibility.Visible : Visibility.Collapsed;
        DisplayComboBox.IsEnabled = selecting;
        OutputScaleComboBox.Visibility = screen ? Visibility.Collapsed : Visibility.Visible;
        OutputScaleComboBox.IsEnabled = selecting;
        UpdateSourceLabel();
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
            flyout.Items.Add(new MenuFlyoutItem { Text = "Buka aplikasi yang ingin disembunyikan terlebih dahulu", IsEnabled = false });
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
        PrivateAppsButton.Content = _privateWindows.Count == 0
            ? "Sembunyikan aplikasi"
            : $"Sembunyikan aplikasi ({_privateWindows.Count})";
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e) => await RunPrimaryActionAsync();

    private async Task RunPrimaryActionAsync()
    {
        if (_sessionTransition) return;
        if (!IsRecordingOnly)
        {
            if (_isLive) await StopSessionAsync();
            else if (TryStartSession()) MinimizeForScreenSession();
            return;
        }

        if (_recorder is not null)
        {
            await StopSessionAsync();
            return;
        }

        var startedCapture = !_isLive;
        if (startedCapture && !TryStartSession()) return;
        if (await RunRecordingChangeAsync(StartRecordingAsync, "Menyiapkan rekaman…"))
        {
            MinimizeForScreenSession();
        }
        else if (startedCapture && _isLive)
        {
            var status = FooterStatusText.Text;
            StopPresentation();
            FooterStatusText.Text = status;
        }
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionTransition) return;
        if (_isLive) await StopSessionAsync();
        else if (TryStartSession()) MinimizeForScreenSession();
    }

    private bool TryStartSession()
    {
        var source = SourceComboBox.SelectedItem as WindowSource;
        var monitor = SourceMonitorComboBox.SelectedItem as DisplayTarget;
        var target = IsRecordingOnly ? null : DisplayComboBox.SelectedItem as DisplayTarget;
        if (IsSafeMirrorMode ? monitor is null : source is null)
        {
            FooterStatusText.Text = IsSafeMirrorMode ? "Pilih layar yang ingin digunakan." : "Pilih aplikasi yang ingin digunakan.";
            return false;
        }
        if (!IsRecordingOnly && target is null)
        {
            FooterStatusText.Text = "Sambungkan dan pilih layar audiens. Untuk menyimpan video saja, pilih mode Rekam.";
            return false;
        }

        try
        {
            StartPresentation(source, monitor, target);
        }
        catch (Exception exception)
        {
            StopPresentation();
            FooterStatusText.Text = $"Tampilan belum dapat dibuka. {exception.Message}";
            SetWindowStatus("Tampilan belum tersedia", PrivacyBrush);
            return false;
        }

        _isLive = true;
        NotesButton.IsChecked = false;
        SetNotesVisible(false);
        _sessionTimer.Start();
        UpdateCaptureModeUi();
        return true;
    }

    private void MinimizeForScreenSession()
    {
        if (!_isSafeMirrorSession || App.MainWindow is not MainWindow window) return;
        window.AutoMinimizeOnDeactivate = true;
        window.MinimizeForPresentation();
    }

    private async Task StopSessionAsync()
    {
        if (_recorder is null)
        {
            StopPresentation();
            return;
        }
        await RunRecordingChangeAsync(async () =>
        {
            var status = await StopRecordingAsync();
            StopPresentation();
            if (status is not null) FooterStatusText.Text = status;
            return true;
        }, "Menyimpan…");
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
        if (_recorder?.IsRecording != true) return;
        UpdateSessionActions();
        var cameraWarning = _recorder.CameraWarning;
        if (cameraWarning != _lastCameraWarning && cameraWarning is not null)
            FooterStatusText.Text = $"{cameraWarning} · rekaman tetap berjalan";
        _lastCameraWarning = cameraWarning;
    }

    private void UpdateRecordingStatusLabels()
    {
        if (ResolveState() != PresentationState.Live) return;
        var recording = _recorder?.IsRecording == true;
        SetWindowStatus(recording ? "Sedang merekam" : _isRecordingOnlySession ? "Pratinjau" : "Presentasi aktif", LiveBrush);
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
        FooterStatusText.Text = $"Pengaturan tersimpan · 1080p {settings.RecordingFrameRate} fps · {settings.RecordingBitrateMbps} Mbps"
            + (settings.CameraEnabled ? " · kamera aktif untuk rekaman berikutnya" : string.Empty);
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is null)
        {
            await RunRecordingChangeAsync(StartRecordingAsync, "Menyiapkan…");
        }
        else
        {
            await RunRecordingChangeAsync(async () =>
            {
                var status = await StopRecordingAsync();
                if (status is not null) FooterStatusText.Text = status;
                return true;
            }, "Menyimpan…");
        }
    }

    private async Task<bool> RunRecordingChangeAsync(Func<Task<bool>> action, string busyLabel)
    {
        if (_sessionTransition) return false;
        _sessionTransition = true;
        (IsRecordingOnly ? PrimaryButton : RecordButton).Content = busyLabel;
        UpdateSessionActions();
        try
        {
            return await action();
        }
        finally
        {
            _sessionTransition = false;
            UpdateSessionActions();
        }
    }

    private async Task<bool> StartRecordingAsync()
    {
        if (!_isLive || _capture is null) return false;
        StorageFile? pendingFile = null;
        var pendingFileWasAutoCreated = false;
        try
        {
            var settings = _settingsService.Current;
            pendingFileWasAutoCreated = !settings.AskRecordingLocation;
            var file = settings.AskRecordingLocation
                ? await AppSettingsService.PickRecordingFileAsync(GetPresenterWindowHandle())
                : await _settingsService.CreateRecordingFileAsync();
            if (file is null)
            {
                FooterStatusText.Text = "Perekaman dibatalkan.";
                return false;
            }
            pendingFile = file;

            if (!_isLive || _capture is null)
                throw new InvalidOperationException("Sesi sudah ditutup. Mulai kembali untuk merekam.");

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
                    throw new InvalidOperationException("Sesi ditutup saat kamera disiapkan. Mulai kembali untuk merekam.");
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
            FooterStatusText.Text = $"Sedang merekam{(recorder.IncludesCamera ? " dengan kamera" : "")} · 1080p {settings.RecordingFrameRate} fps.";
            UpdateRecordingStatusLabels();
            _ = WatchRecordingAsync(recorder);
            return true;
        }
        catch (Exception exception)
        {
            if (pendingFileWasAutoCreated) await DeleteEmptyRecordingAsync(pendingFile);
            FooterStatusText.Text = $"Rekaman belum dapat dimulai. {exception.Message}";
            return false;
        }
    }

    private async Task<string?> StopRecordingAsync()
    {
        var recorder = _recorder;
        if (recorder is null) return null;
        var file = _recordingFile;
        _recorder = null;
        _recordingFile = null;
        _recordingFileWasAutoCreated = false;

        try
        {
            await recorder.DisposeAsync();
            return file is null ? "Rekaman selesai." : $"Rekaman tersimpan · {file.Name}";
        }
        catch (Exception exception)
        {
            return $"Rekaman belum dapat disimpan. {exception.Message}";
        }
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
        UpdateSessionActions();
        UpdateRecordingStatusLabels();
        FooterStatusText.Text = failure is null
            ? "Rekaman terhenti. Tekan Rekam untuk memulai video baru."
            : $"Rekaman terhenti. {failure.Message}";
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
            throw new InvalidOperationException("Pilih layar audiens yang berbeda dari layar sumber, atau gunakan mode Rekam.");
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
        _notesAudience = target;
        ApplyNoteCaptureRules();
        SetNotesEditingEnabled(false);

        if (_clientDisplay is not null)
        {
            WindowCatalogService.MoveWindowsToDisplay(_clientDisplay.Handle, _presenterDisplay!.Bounds);
            _windowRescueTimer.Start();
        }

        if (_isSafeMirrorSession)
        {
            var primaryDisplay = _presenterDisplay
                ?? throw new InvalidOperationException("Layar yang dipilih tidak ditemukan. Sambungkan layar dan pilih kembali.");
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

        if (target is not null)
        {
            var previewMode = target.IsPrimary && _displays.Count == 1;
            _outputWindow = new OutputWindow(_capture, target, previewMode, _outputScaleMode, _outputAdaptiveZoom);
            _outputWindow.Closed += OutputWindow_Closed;
            _outputWindow.Activate();
            _outputWindow.ApplyPlacement();
        }
        (App.MainWindow as MainWindow)?.Activate();
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

        _notesAudience = null;
        ApplyNoteCaptureRules();
        SetNotesEditingEnabled(true);

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
        if (IsForegroundPrivate() || IsNoteEditorOnRecordedDisplay() || WindowCatalogService.IsApplicationSwitcherActive()
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
        if (!_isLive) return;
        if (!_isPrivacy && ClientStateOverlay.Visibility == Visibility.Visible) UpdatePreview();
        LivePreviewCanvas.Invalidate();
    }

    private void Capture_SourceClosed(object? sender, EventArgs e)
    {
        HandleSourceLost("Aplikasi yang ditampilkan telah ditutup.");
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
        FooterStatusText.Text = $"Tampilan terputus. {detail}";
    }

    private async void OutputWindow_Closed(object sender, WindowEventArgs args)
    {
        _outputWindow = null;
        var status = await StopRecordingAsync();
        StopPresentation();
        if (status is not null) FooterStatusText.Text = status;
    }

    private void LivePreviewCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args) => _capture?.Draw(sender, args);

    private async void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        DisposeFloatingNotes();
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

        FooterStatusIcon.Foreground = brush;
        UpdateSessionActions();
        UpdatePreview();

        switch (state)
        {
            case PresentationState.Live:
                _outputWindow?.SetMode(ClientOutputMode.Live);
                UpdateRecordingStatusLabels();
                FooterStatusText.Text = _isProtectedPublicCapture
                    ? "Aplikasi yang disembunyikan tidak ikut tampil."
                    : _recorder?.IsRecording == true
                        ? "Sedang merekam. Klik Stop rekam untuk menyimpan video."
                        : _isRecordingOnlySession
                            ? "Pratinjau aktif. Klik Mulai rekam untuk menyimpan video."
                            : "Presentasi aktif. Klik Rekam jika Anda juga ingin menyimpan video.";
                break;
            case PresentationState.Frozen:
                var waitingForPublicApp = _isAutoHeld && !(_capture?.HasFrame ?? false);
                _outputWindow?.SetMode(
                    waitingForPublicApp ? ClientOutputMode.Privacy : ClientOutputMode.Frozen,
                    waitingForPublicApp ? "Buka aplikasi yang ingin ditampilkan" : null);
                FooterStatusText.Text = waitingForPublicApp
                    ? "Buka aplikasi yang ingin ditampilkan untuk melanjutkan."
                    : _isAutoHeld ? "Aplikasi yang disembunyikan tidak ikut tampil."
                    : "Freeze aktif. Klik Freeze lagi untuk melanjutkan.";
                SetWindowStatus(waitingForPublicApp ? "Menyiapkan tampilan" : "Freeze aktif", FreezeBrush);
                break;
            case PresentationState.Privacy:
                _outputWindow?.SetMode(ClientOutputMode.Privacy);
                FooterStatusText.Text = "Privasi aktif. Klik Privasi lagi untuk menampilkan gambar kembali.";
                SetWindowStatus("Privasi aktif", PrivacyBrush);
                break;
            default:
                FooterStatusText.Text = IsRecordingOnly
                    ? "Pilih layar atau aplikasi, lalu klik Mulai rekam."
                    : "Pilih sumber dan layar audiens, lalu klik Mulai presentasi.";
                SetWindowStatus("Siap", MutedBrush);
                break;
        }
    }

    private void UpdateSessionActions()
    {
        var recording = _recorder is not null;
        var elapsed = DateTimeOffset.Now - _recordingStartedAt;
        var stopRecordingLabel = $"Stop rekam · {elapsed:mm\\:ss}";
        PrimaryButton.IsEnabled = !_sessionTransition;
        PreviewButton.IsEnabled = !_sessionTransition;
        SettingsButton.IsEnabled = !_sessionTransition;
        RecordButton.IsEnabled = !_sessionTransition;
        PreviewButton.Visibility = IsRecordingOnly && !recording ? Visibility.Visible : Visibility.Collapsed;
        PreviewButton.Content = _isLive ? "Tutup pratinjau" : "Lihat pratinjau";
        SessionControls.Visibility = _isLive ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.Visibility = IsRecordingOnly ? Visibility.Collapsed : Visibility.Visible;
        if (_sessionTransition) return;

        PrimaryButton.Content = IsRecordingOnly
            ? recording ? stopRecordingLabel : "Mulai rekam"
            : _isLive ? "Stop presentasi" : "Mulai presentasi";
        RecordButton.Content = recording ? stopRecordingLabel : "Rekam";
    }

    private void UpdatePreview()
    {
        var frameVisible = _isLive && !_isPrivacy && _capture?.HasFrame == true;
        LivePreviewCanvas.Visibility = frameVisible ? Visibility.Visible : Visibility.Collapsed;
        ClientStateOverlay.Visibility = frameVisible ? Visibility.Collapsed : Visibility.Visible;
        if (frameVisible) return;

        if (_isPrivacy)
        {
            ClientStateIcon.Glyph = "\uE890";
            ClientStateTitle.Text = "Privasi aktif";
            ClientStateSubtitle.Text = "Tampilan disembunyikan sementara.";
            return;
        }

        ClientStateIcon.Glyph = "\uE7F4";
        ClientStateTitle.Text = _isLive ? "Menyiapkan tampilan…" : IsRecordingOnly ? "Siap merekam" : "Siap presentasi";
        ClientStateSubtitle.Text = _isLive
            ? "Buka layar atau aplikasi yang ingin ditampilkan."
            : IsRecordingOnly
                ? "Tampilan sumber akan terlihat di sini saat pratinjau atau rekaman berjalan."
                : "Sumber presentasi akan terlihat di sini.";
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
