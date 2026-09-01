using Clario_Presenter.Capture;
using Clario_Presenter.Recording;
using Clario_Presenter.Services;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Clario_Presenter;

public sealed partial class MainPage : Page
{
    private readonly ObservableCollection<WindowSource> _sources = [];
    private readonly ObservableCollection<DisplayTarget> _displays = [];
    private readonly HashSet<nint> _privateWindows = [];
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _privacyWatchTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _windowRescueTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private TimeSpan _elapsed;
    private bool _isLive;
    private bool _isFrozen;
    private bool _isPrivacy;
    private bool _isAutoHeld;
    private bool _isSafeMirrorSession;
    private bool _isProtectedPublicCapture;
    private nint _lastActivePrivateWindow;
    private nint _lastPublicWindow;
    private nint _mirrorMonitorHandle;
    private DisplayTarget? _clientDisplay;
    private DisplayTarget? _presenterDisplay;
    private CaptureSessionService? _capture;
    private OutputWindow? _outputWindow;
    private OutputRecordingService? _recorder;
    private StorageFile? _recordingFile;
    private DateTimeOffset _recordingStartedAt;
    private bool _recordingTransition;

    private SolidColorBrush LiveBrush => ResourceBrush("ClarioLiveBrush");
    private SolidColorBrush FreezeBrush => ResourceBrush("ClarioFreezeBrush");
    private SolidColorBrush PrivacyBrush => ResourceBrush("ClarioPrivacyBrush");
    private SolidColorBrush MutedBrush => ResourceBrush("ClarioSecondaryTextBrush");

    public MainPage()
    {
        InitializeComponent();
        SourceComboBox.ItemsSource = _sources;
        DisplayComboBox.ItemsSource = _displays;
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
        _displays.Clear();
        foreach (var display in DisplayCatalogService.GetDisplays()) _displays.Add(display);
        DisplayComboBox.SelectedItem = _displays.FirstOrDefault(display => !display.IsPrimary)
                                       ?? _displays.FirstOrDefault();
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

    private void CaptureModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceComboBox is null || PrivateAppsButton is null) return;
        UpdateCaptureModeUi();
    }

    private void UpdateCaptureModeUi()
    {
        var safeMirror = IsSafeMirrorMode;
        SourceComboBox.IsEnabled = !safeMirror && !_isLive;
        PrivateAppsButton.IsEnabled = safeMirror;
        SourcePrivacyBadgeText.Text = safeMirror ? "MIRROR AMAN" : "JENDELA PRIVAT";

        if (safeMirror)
        {
            ActiveSourceTitle.Text = "Layar utama · Mirror Aman";
            ActiveSourceSubtitle.Text = "Semua aktivitas tampil, kecuali Clario dan aplikasi privat";
            BrowserAddressText.Text = "Monitor utama · Pas Otomatis menampilkan seluruh layar";
            DemoHeadingText.Text = "Mirror Aman";
            MiniOutputTitle.Text = "Layar utama";
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
        if (!_isLive && (DisplayComboBox.SelectedItem is null
            || (!IsSafeMirrorMode && SourceComboBox.SelectedItem is null)))
        {
            FooterStatusText.Text = "Pilih jendela sumber dan monitor output terlebih dahulu";
            return;
        }

        if (!_isLive)
        {
            var source = SourceComboBox.SelectedItem as WindowSource;
            if (DisplayComboBox.SelectedItem is not DisplayTarget target
                || (!IsSafeMirrorMode && source is null))
            {
                FooterStatusText.Text = "Pilih jendela sumber dan monitor output terlebih dahulu";
                return;
            }

            try
            {
                StartPresentation(source, target);
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
        }
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
        try
        {
            if (_recorder?.IsRecording == true)
            {
                await StopRecordingAsync();
                return;
            }

            if (!_isLive || _outputWindow is null)
            {
                RecordButton.IsChecked = false;
                FooterStatusText.Text = "Mulai Live sebelum merekam";
                return;
            }

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                SuggestedFileName = $"Clario-{DateTime.Now:yyyyMMdd-HHmmss}"
            };
            picker.FileTypeChoices.Add("Video MP4", [".mp4"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, GetPresenterWindowHandle());

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                RecordButton.IsChecked = false;
                return;
            }

            var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var recorder = new OutputRecordingService();
            try
            {
                await recorder.StartAsync(_outputWindow.WindowHandle, stream);
            }
            catch
            {
                await recorder.DisposeAsync();
                throw;
            }

            _recorder = recorder;
            _recordingFile = file;
            _recordingStartedAt = DateTimeOffset.Now;
            RecordButton.IsChecked = true;
            RecordButtonText.Text = "REC 00:00";
            RecordButtonDot.Fill = PrivacyBrush;
            FooterStatusText.Text = "Merekam output klien · 1080p 60 fps · H.264";
            _ = WatchRecordingAsync(recorder);
        }
        catch (Exception exception)
        {
            RecordButton.IsChecked = false;
            RecordButtonText.Text = "Rekam";
            FooterStatusText.Text = $"Rekaman gagal dimulai · {exception.Message}";
        }
        finally
        {
            _recordingTransition = false;
            RecordButton.IsEnabled = _isLive;
        }
    }

    private async Task StopRecordingAsync()
    {
        var recorder = _recorder;
        var file = _recordingFile;
        _recorder = null;
        _recordingFile = null;
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
            failure = exception;
        }

        // A normal StopRecordingAsync clears _recorder before signalling the
        // media pipeline. Only handle completion here when the encoder ended
        // on its own (for example after a driver/device failure).
        if (!ReferenceEquals(_recorder, recorder)) return;

        _recorder = null;
        _recordingFile = null;
        try
        {
            await recorder.DisposeAsync();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        RecordButton.IsChecked = false;
        RecordButtonText.Text = "Rekam";
        RecordButton.IsEnabled = _isLive;
        FooterStatusText.Text = failure is null
            ? "Rekaman berhenti lebih awal · silakan mulai ulang REC"
            : $"Encoder berhenti · {failure.Message}";
    }

    private void StartPresentation(WindowSource? source, DisplayTarget target)
    {
        _capture = new CaptureSessionService();
        _capture.FrameAvailable += Capture_FrameAvailable;
        _capture.SourceClosed += Capture_SourceClosed;
        _capture.CaptureFailed += Capture_CaptureFailed;
        _isSafeMirrorSession = IsSafeMirrorMode;
        _presenterDisplay = _displays.FirstOrDefault(display => display.IsPrimary);
        _clientDisplay = !target.IsPrimary && _presenterDisplay is not null ? target : null;

        if (_clientDisplay is not null)
        {
            WindowCatalogService.MoveWindowsToDisplay(_clientDisplay.Handle, _presenterDisplay!.Bounds);
            _windowRescueTimer.Start();
        }

        if (_isSafeMirrorSession)
        {
            var primaryDisplay = _presenterDisplay
                ?? throw new InvalidOperationException("Monitor utama tidak ditemukan.");
            // Begin from a known-safe desktop. Selected private windows are still
            // available from the taskbar and will trigger auto-hold when restored.
            _isAutoHeld = true;
            _mirrorMonitorHandle = primaryDisplay.Handle;
            WindowCatalogService.MinimizeWindows(_privateWindows);
            _capture.FrameHoldPredicate = () => !_isProtectedPublicCapture
                && (_isAutoHeld || IsForegroundPrivate());
            _capture.StartMonitor(primaryDisplay.Handle);
            _privacyWatchTimer.Start();
        }
        else
        {
            _capture.Start(source!.Handle);
        }

        LivePreviewCanvas.Visibility = Visibility.Visible;
        var previewMode = target.IsPrimary && _displays.Count == 1;
        var outputScaleMode = OutputScaleComboBox.SelectedIndex == 1
            ? CaptureScaleMode.Fill
            : CaptureScaleMode.Fit;
        _outputWindow = new OutputWindow(_capture, target, previewMode, outputScaleMode);
        _outputWindow.Closed += OutputWindow_Closed;
        _outputWindow.Activate();
        _outputWindow.ApplyPlacement();
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
        _isProtectedPublicCapture = false;
        _lastActivePrivateWindow = nint.Zero;
        _lastPublicWindow = nint.Zero;
        _mirrorMonitorHandle = nint.Zero;
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

        return _privateWindows.Contains(foregroundWindow)
            && WindowCatalogService.IsWindowPresentable(foregroundWindow);
    }

    private void PrivacyWatchTimer_Tick(object? sender, object e)
    {
        if (!_isSafeMirrorSession) return;

        var foregroundWindow = WindowCatalogService.GetForegroundWindowHandle();
        var presenterActive = foregroundWindow == GetPresenterWindowHandle();
        var privateWindowActive = _privateWindows.Contains(foregroundWindow)
            && WindowCatalogService.IsWindowPresentable(foregroundWindow);
        var protectedViewNeeded = presenterActive || privateWindowActive;

        if (privateWindowActive)
        {
            _lastActivePrivateWindow = foregroundWindow;
        }
        else if (_lastActivePrivateWindow != nint.Zero)
        {
            // Keep the private window from becoming visible behind a smaller
            // public window, then allow the public capture to continue.
            WindowCatalogService.MinimizeWindow(_lastActivePrivateWindow);
            _lastActivePrivateWindow = nint.Zero;
        }

        if (!protectedViewNeeded && WindowCatalogService.IsWindowCapturable(foregroundWindow))
        {
            _lastPublicWindow = foregroundWindow;
        }

        var stateChanged = false;
        if (protectedViewNeeded)
        {
            if (!_isProtectedPublicCapture)
            {
                if (!_isAutoHeld)
                {
                    _isAutoHeld = true;
                    stateChanged = true;
                }
                if (TrySwitchToPublicWindow())
                {
                    _isAutoHeld = false;
                    stateChanged = true;
                }
            }
        }
        else
        {
            if (_isProtectedPublicCapture)
            {
                _isAutoHeld = true;
                stateChanged = true;
                TrySwitchBackToMirror();
            }

            if (_isAutoHeld)
            {
                _isAutoHeld = false;
                stateChanged = true;
            }
        }

        if (stateChanged) UpdatePresentationState();
    }

    private bool TrySwitchToPublicWindow()
    {
        if (_capture is null
            || !WindowCatalogService.PrepareWindowForBackgroundCapture(_lastPublicWindow)) return false;

        try
        {
            // The monitor remains held until the safe window capture is ready.
            _capture.Start(_lastPublicWindow, keepLastFrame: true);
            _isProtectedPublicCapture = true;
            return true;
        }
        catch
        {
            _isProtectedPublicCapture = false;
            TrySwitchBackToMirror();
            return false;
        }
    }

    private bool TrySwitchBackToMirror()
    {
        if (_capture is null || _mirrorMonitorHandle == nint.Zero) return false;

        try
        {
            _capture.StartMonitor(_mirrorMonitorHandle, keepLastFrame: true);
            _isProtectedPublicCapture = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static nint GetPresenterWindowHandle() => App.MainWindow is null
        ? nint.Zero
        : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);

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
        if (RecoverProtectedCapture()) return;
        HandleSourceLost("Jendela sumber telah ditutup.");
    }

    private void Capture_CaptureFailed(object? sender, CaptureFailureEventArgs e)
    {
        if (RecoverProtectedCapture()) return;
        var detail = $"{e.Message} {e.Exception.Message}";
        HandleSourceLost(detail);
    }

    private bool RecoverProtectedCapture()
    {
        if (!_isSafeMirrorSession || !_isProtectedPublicCapture) return false;
        _isProtectedPublicCapture = false;
        _lastPublicWindow = nint.Zero;
        _isAutoHeld = true;
        var recovered = TrySwitchBackToMirror();
        UpdatePresentationState();
        return recovered;
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
        LiveButtonText.Text = _isLive ? "Stop" : "Mulai Live";

        switch (state)
        {
            case PresentationState.Live:
                _outputWindow?.SetMode(ClientOutputMode.Live);
                ClientStatusText.Text = _isProtectedPublicCapture ? "LIVE AMAN" : "LIVE";
                ClientStateOverlay.Visibility = Visibility.Collapsed;
                FooterStatusText.Text = _isProtectedPublicCapture
                    ? "Live Terlindungi · aplikasi publik tetap berjalan saat catatan privat dibuka"
                    : _isSafeMirrorSession
                        ? "Mirror Aman · layar utama menyesuaikan monitor klien secara otomatis"
                        : "Live · monitor klien hanya menerima jendela terpilih";
                SetWindowStatus(
                    _isProtectedPublicCapture ? "Live terlindungi" : "Live ke monitor klien",
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
                FooterStatusText.Text = IsSafeMirrorMode
                    ? "Siap · Mirror Aman akan menyalin layar utama dengan skala otomatis"
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

    private void SetWindowStatus(string label, SolidColorBrush brush)
    {
        if (App.MainWindow is MainWindow window) window.SetSessionStatus(label, brush);
    }

    private static SolidColorBrush ResourceBrush(string key) => (SolidColorBrush)Application.Current.Resources[key];

    private enum PresentationState { Ready, Live, Frozen, Privacy }
}
