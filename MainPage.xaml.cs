using Clario_Presenter.Audio;
using Clario_Presenter.Capture;
using Clario_Presenter.Camera;
using Clario_Presenter.Recording;
using Clario_Presenter.Services;
using Clario_Presenter.Settings;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private bool _presenterExcludedFromCapture;
    private uint? _presenterPreviousCaptureAffinity;
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
    private bool _sourceDisplayNeedsSelection;
    private bool _audienceDisplayNeedsSelection;
    private StorageFile? _lastSavedRecording;
    private string? _lastRecordingWarning;

    private SolidColorBrush LiveBrush => ResourceBrush("ClarioLiveBrush");
    private SolidColorBrush FreezeBrush => ResourceBrush("ClarioFreezeBrush");
    private SolidColorBrush PrivacyBrush => ResourceBrush("ClarioPrivacyBrush");
    private SolidColorBrush RecordingBrush => ResourceBrush("ClarioRecordingBrush");
    private SolidColorBrush MutedBrush => ResourceBrush("ClarioSecondaryTextBrush");

    public MainPage()
    {
        InitializeComponent();
        InitializeFloatingNotes();
        _studioTimer.Tick += StudioTimer_Tick;
        SourceComboBox.ItemsSource = _sources;
        DisplayComboBox.ItemsSource = _audienceDisplays;
        SourceMonitorComboBox.ItemsSource = _displays;
        _sessionTimer.Tick += SessionTimer_Tick;
        _privacyWatchTimer.Tick += PrivacyWatchTimer_Tick;
        _windowRescueTimer.Tick += WindowRescueTimer_Tick;
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (App.MainWindow is { } window)
        {
            window.AppWindow.Closing += PresenterWindow_Closing;
            window.AppWindow.Changed += StudioWindow_Changed;
            UpdateStudioWindowVisibility(window.AppWindow);
        }
        RefreshDisplays();
        UpdateCaptureModeUi();
        await RestoreUpdateNotesAsync();
        if (!_isLoaded) return;
        ShowInstallationResult();
        _updates.Changed += ShowUpdateNotice;
        _updateTimer.Tick += Updates_Tick;
        _updateTimer.Start();
    }

    private void RefreshSources()
    {
        var selectedHandle = (SourceComboBox.SelectedItem as WindowSource)?.Handle;
        _sources.Clear();

        foreach (var source in WindowCatalogService.GetPresentableWindows())
        {
            _sources.Add(source);
        }

        SourceComboBox.SelectedItem = _sources.FirstOrDefault(item => item.Handle == selectedHandle);
    }

    private void RefreshDisplays()
    {
        var sourceDeviceName = (SourceMonitorComboBox.SelectedItem as DisplayTarget)?.DeviceName;
        _refreshingDisplays = true;
        try
        {
            _displays.Clear();
            foreach (var display in DisplayCatalogService.GetDisplays()) _displays.Add(display);
            var previous = FindDisplay(_displays, sourceDeviceName);
            if (sourceDeviceName is not null) _sourceDisplayNeedsSelection = previous is null;
            SourceMonitorComboBox.SelectedItem = previous
                ?? (_sourceDisplayNeedsSelection ? null : _displays.FirstOrDefault());
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
        if (selectedDeviceName is not null)
            _audienceDisplayNeedsSelection = FindDisplay(_displays, selectedDeviceName) is null;
        DisplayComboBox.SelectedItem = FindDisplay(_audienceDisplays, selectedDeviceName)
            ?? (_audienceDisplayNeedsSelection ? null :
                _audienceDisplays.FirstOrDefault(display => !display.IsPrimary) ?? _audienceDisplays.FirstOrDefault());
    }

    private static DisplayTarget? FindDisplay(IEnumerable<DisplayTarget> displays, string? deviceName) =>
        displays.FirstOrDefault(display => display.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

    private void DisplayComboBox_DropDownOpened(object sender, object e)
    {
        if (!_isLive) RefreshDisplays();
    }

    private void SourceComboBox_DropDownOpened(object sender, object e)
    {
        if (!_isLive) RefreshSources();
    }

    private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSourceLabel();

    private void DisplayComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisplayComboBox.SelectedItem is DisplayTarget display)
        {
            _audienceDisplayNeedsSelection = false;
            ToolTipService.SetToolTip(DisplayComboBox, display.DisplayName);
        }
        else ToolTipService.SetToolTip(DisplayComboBox, null);
        UpdateSourceLabel();
    }

    private void SourceMonitorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingDisplays || _isLive || !_isLoaded) return;
        if (SourceMonitorComboBox.SelectedItem is DisplayTarget) _sourceDisplayNeedsSelection = false;
        RefreshAudienceDisplays();
        UpdateSourceLabel();
    }

    private void UpdateSourceLabel()
    {
        if (ActiveSourceTitle is null) return;
        var monitor = SourceMonitorComboBox.SelectedItem as DisplayTarget;
        var application = SourceComboBox.SelectedItem as WindowSource;
        var target = IsRecordingOnly ? null : DisplayComboBox.SelectedItem as DisplayTarget;
        var sourceLabel = IsSafeMirrorMode ? monitor?.ShortDisplayName ?? "Pilih layar" : application?.Title ?? "Pilih aplikasi";
        ActiveSourceTitle.Text = target is null ? sourceLabel : $"{sourceLabel}  →  {target.ShortDisplayName}";
        ToolTipService.SetToolTip(ActiveSourceTitle, ActiveSourceTitle.Text);
        ToolTipService.SetToolTip(SourceMonitorComboBox, monitor?.DisplayName);
        ToolTipService.SetToolTip(SourceComboBox, application?.DisplayName);
    }

    private void RefreshSources_Click(object sender, RoutedEventArgs e)
    {
        RefreshSources();
        if (_sources.Count == 0)
            FooterStatusText.Text = "Buka aplikasi yang ingin dipilih, lalu perbarui daftar.";
    }

    private bool IsSafeMirrorMode => CaptureModePicker.SelectedIndex == 0;
    private bool IsRecordingOnly => SessionModePicker.SelectedIndex == 0;

    private void SessionModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        UpdateCaptureModeUi();
    }

    private void CaptureModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
        SessionModePicker.IsEnabled = selecting;
        CaptureModePicker.IsEnabled = selecting;
        SourceComboBox.Visibility = screen ? Visibility.Collapsed : Visibility.Visible;
        SourceComboBox.IsEnabled = selecting;
        SourceMonitorComboBox.Visibility = screen ? Visibility.Visible : Visibility.Collapsed;
        SourceMonitorComboBox.IsEnabled = selecting;
        RefreshSourcesButton.Visibility = screen ? Visibility.Collapsed : Visibility.Visible;
        RefreshSourcesButton.IsEnabled = selecting;
        PrivateAppsButton.Visibility = screen && selecting ? Visibility.Visible : Visibility.Collapsed;
        OutputOptionsButton.Visibility = !screen && selecting ? Visibility.Visible : Visibility.Collapsed;
        DisplayComboBox.Visibility = presentation ? Visibility.Visible : Visibility.Collapsed;
        DisplayComboBox.IsEnabled = selecting;
        LayoutNoteEditor();
        UpdateSourceLabel();
        UpdatePresentationState();
    }

    private async void PrivateAppsButton_Click(object sender, RoutedEventArgs e)
    {
        var applications = WindowCatalogService.GetPresentableWindows()
            .OrderBy(application => application.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var options = new StackPanel { Spacing = 4 };
        var selectionText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var checks = applications.Select(application => new CheckBox
        {
            Content = new TextBlock { Text = application.DisplayName, TextWrapping = TextWrapping.Wrap },
            IsChecked = _privateWindows.Contains(application.Handle),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40
        }).ToArray();

        void UpdateSelectionCount() => selectionText.Text = checks.Length == 0
            ? "Buka aplikasi yang ingin disembunyikan terlebih dahulu."
            : $"{checks.Count(check => check.IsChecked == true)} dipilih. Pilihan berlaku per jendela."
                + (_settingsService.Current.ComputerAudioEnabled ? " Suara komputer tetap mengikuti keluaran yang dipilih." : "");

        foreach (var check in checks)
        {
            check.Checked += (_, _) => UpdateSelectionCount();
            check.Unchecked += (_, _) => UpdateSelectionCount();
            options.Children.Add(check);
        }
        UpdateSelectionCount();

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(selectionText);
        content.Children.Add(new ScrollViewer
        {
            Content = options,
            MaxHeight = 300,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "Sembunyikan aplikasi",
            PrimaryButtonText = "Terapkan",
            CloseButtonText = "Batal",
            Content = content
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            for (var index = 0; index < checks.Length; index++)
            {
                var handle = applications[index].Handle;
                if (checks[index].IsChecked == true) _privateWindows.Add(handle);
                else _privateWindows.Remove(handle);
            }
            PrivateAppsButton.Content = _privateWindows.Count == 0
                ? "Sembunyikan aplikasi"
                : $"Sembunyikan aplikasi ({_privateWindows.Count})";
        }
        PrivateAppsButton.Focus(FocusState.Programmatic);
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e) => await RunPrimaryActionAsync();

    private async Task RunPrimaryActionAsync()
    {
        if (_sessionTransition || _settingsDialogOpen) return;
        if (!IsRecordingOnly)
        {
            if (_recorder is not null && _outputWindow is not null) _outputWindow.Close();
            else if (_isLive) await StopSessionAsync();
            else TryStartSession();
            return;
        }

        if (_recorder is not null)
        {
            await StopSessionAsync();
            return;
        }

        var startedCapture = !_isLive;
        var started = await RunRecordingChangeAsync(async () =>
            (!startedCapture || TryStartSession()) && await StartRecordingAsync(), "Menyiapkan rekaman…");
        if (!started && startedCapture && _isLive)
        {
            var status = FooterStatusText.Text;
            StopPresentation();
            FooterStatusText.Text = status;
        }
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionTransition || _settingsDialogOpen) return;
        if (_recorder is not null)
        {
            _recordingPreviewOpen = !_recordingPreviewOpen;
            UpdateSessionActions();
            UpdatePreview();
            await SyncStudioPreviewAsync();
            if (ShowStudioInputs) LivePreviewCanvas.Invalidate();
        }
        else if (_isLive) await StopSessionAsync();
        else TryStartSession();
    }

    private bool TryStartSession()
    {
        if (_handoff) return false;
        _updates.Pause();
        RefreshDisplays();
        var source = SourceComboBox.SelectedItem as WindowSource;
        var monitor = SourceMonitorComboBox.SelectedItem as DisplayTarget;
        var target = IsRecordingOnly ? null : DisplayComboBox.SelectedItem as DisplayTarget;
        if (IsSafeMirrorMode && monitor is null)
        {
            FooterStatusText.Text = _sourceDisplayNeedsSelection
                ? "Layar sumber terputus. Pilih layar lagi."
                : "Pilih layar yang ingin digunakan.";
            SourceMonitorComboBox.Focus(FocusState.Programmatic);
            return false;
        }
        if (!IsSafeMirrorMode && (source is null || !WindowCatalogService.PrepareWindowForBackgroundCapture(source.Handle)))
        {
            RefreshSources();
            FooterStatusText.Text = "Pilih jendela aplikasi yang masih terbuka.";
            SourceComboBox.Focus(FocusState.Programmatic);
            return false;
        }
        if (!IsRecordingOnly && target is null)
        {
            FooterStatusText.Text = _audienceDisplayNeedsSelection
                ? "Layar audiens terputus. Pilih layar audiens lagi."
                : "Sambungkan dan pilih layar audiens, atau pilih mode Rekam.";
            DisplayComboBox.Focus(FocusState.Programmatic);
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
        UpdateCaptureModeUi();
        return true;
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

    private void NotesButton_Click(object sender, RoutedEventArgs e)
    {
        SetNotesVisible(NotesButton.IsChecked == true);
        if (NotesPanel.Visibility == Visibility.Visible)
        {
            if (NoteReader.Visibility == Visibility.Visible) NoteReaderScroll.Focus(FocusState.Programmatic);
            else FreeNotesTextBox.Focus(FocusState.Programmatic);
        }
    }

    private void CollapseNotes_Click(object sender, RoutedEventArgs e) => CloseNoteEditor();

    private void CloseNotesAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (NotesPanel.Visibility != Visibility.Visible) return;
        CloseNoteEditor();
        e.Handled = true;
    }

    private void CloseNoteEditor()
    {
        NotesButton.IsChecked = false;
        SetNotesVisible(false);
        NotesButton.Focus(FocusState.Programmatic);
    }

    private void SetNotesVisible(bool visible)
    {
        NotesPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        LayoutNoteEditor();
    }

    private void StudioGrid_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutNoteEditor();

    private void LayoutNoteEditor()
    {
        var visible = NotesPanel.Visibility == Visibility.Visible;
        var docked = visible && WorkspaceGrid.ActualWidth >= 1080;
        var noteOverlay = visible && !docked;
        var setupVisibility = _isLive || noteOverlay ? Visibility.Collapsed : Visibility.Visible;
        CaptureModePicker.Visibility = setupVisibility;
        SourceOptions.Visibility = setupVisibility;
        var previewVisibility = noteOverlay || (!_isLive && StudioGrid.ActualHeight < 120)
            ? Visibility.Collapsed : Visibility.Visible;
        if (PreviewPanel.Visibility != previewVisibility)
        {
            PreviewPanel.Visibility = previewVisibility;
            if (_isLoaded) _ = SyncStudioPreviewAsync();
            if (ShowStudioInputs)
            {
                UpdatePreview();
                LivePreviewCanvas.Invalidate();
            }
        }
        PreviewHeading.Visibility = noteOverlay ? Visibility.Collapsed : Visibility.Visible;
        NotesColumn.Width = new GridLength(docked ? 320 : 0);
        StudioGrid.ColumnSpacing = docked ? 12 : 0;
        Grid.SetColumn(NotesPanel, docked ? 1 : 0);
        NotesPanel.HorizontalAlignment = docked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        NotesPanel.Width = docked ? double.NaN : Math.Min(340, StudioGrid.ActualWidth);
        if (visible) NoteContentArea.Height = Math.Max(80, StudioGrid.ActualHeight - 186);
    }

    private void SessionTimer_Tick(object? sender, object e)
    {
        if (_recorder?.IsRecording != true) return;
        UpdateRecordingStatusLabels();
        var warning = _recorder.AudioWarning ?? _recorder.CameraWarning;
        if (warning != _lastRecordingWarning && warning is not null)
            FooterStatusText.Text = $"{warning} Rekaman tetap berjalan.";
        _lastRecordingWarning = warning;
    }

    private void UpdateRecordingStatusLabels()
    {
        var state = ResolveState();
        if (_recorder?.IsRecording == true)
        {
            var elapsed = (DateTimeOffset.Now - _recordingStartedAt).ToString(@"hh\:mm\:ss");
            var label = state switch
            {
                PresentationState.Frozen when _isFrozen => $"Merekam {elapsed} · Freeze",
                PresentationState.Privacy => $"Merekam {elapsed} · Privasi",
                _ => $"Merekam {elapsed}"
            };
            SetWindowStatus(label, RecordingBrush);
            return;
        }

        switch (state)
        {
            case PresentationState.Live:
                SetWindowStatus(_isRecordingOnlySession ? "Preview" : "Presentasi aktif", LiveBrush);
                break;
            case PresentationState.Frozen:
                SetWindowStatus(_isFrozen ? "Freeze aktif" : "Menyiapkan tampilan", FreezeBrush);
                break;
            case PresentationState.Privacy:
                SetWindowStatus("Privasi aktif", ResourceBrush("ClarioPrivateStatusBrush"));
                break;
            default:
                SetWindowStatus("Siap", MutedBrush);
                break;
        }
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
        if (_sessionTransition || _handoff) return false;
        _updates.Pause();
        _sessionTransition = true;
        (IsRecordingOnly ? PrimaryButton : RecordButton).Content = busyLabel;
        UpdateSessionActions();
        try
        {
            await SyncStudioPreviewAsync();
            return await action();
        }
        finally
        {
            _sessionTransition = false;
            UpdateSessionActions();
            UpdatePreview();
            await SyncStudioPreviewAsync();
        }
    }

    private async Task<bool> StartRecordingAsync()
    {
        if (!_isLive || _capture is null) return false;
        _recordingPreviewOpen = true;
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
                        PreviewCameraOptions(settings),
                        new RecordingAudioOptions(settings.ComputerAudioEnabled, settings.PlaybackDeviceId,
                            settings.MicrophoneEnabled, settings.MicrophoneDeviceId, settings.MicrophoneSpeechProfile,
                            settings.WarmNarration, settings.ReduceFanNoise, settings.BoostedMicrophones)));
                if (!_isLive || _capture is null)
                    throw new InvalidOperationException("Sesi ditutup saat rekaman disiapkan. Mulai kembali untuk merekam.");
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
            _lastRecordingWarning = null;
            var camera = recorder.IncludesCamera ? " · Kamera aktif" : "";
            var audio = recorder.IncludesAudio ? " · Audio aktif" : "";
            FooterStatusText.Text = settings.MicrophoneEnabled
                ? $"Mikrofon siap. Silakan bicara · 1080p {settings.RecordingFrameRate} fps{camera}."
                : $"Sedang merekam · 1080p {settings.RecordingFrameRate} fps{camera}{audio}.";
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
            _lastSavedRecording = file;
            return file is null ? "Rekaman selesai." : $"Rekaman tersimpan · {file.Name}";
        }
        catch (Exception exception)
        {
            return $"Rekaman belum dapat disimpan. {exception.Message}";
        }
    }

    private async void OpenRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSavedRecording is not { } file || Path.GetDirectoryName(file.Path) is not { } path) return;
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(path);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder))
                FooterStatusText.Text = $"Folder belum dapat dibuka. {path}";
        }
        catch (Exception exception)
        {
            FooterStatusText.Text = $"Folder belum dapat dibuka. {exception.Message}";
        }
    }

    private async void PlayRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSavedRecording is not { } file) return;
        try
        {
            if (!await Windows.System.Launcher.LaunchFileAsync(file))
                FooterStatusText.Text = "Video belum bisa diputar. Klik Buka folder untuk melihat filenya.";
        }
        catch (Exception exception)
        {
            FooterStatusText.Text = $"Video belum bisa diputar. {exception.Message}";
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
        failure = recorder.Failure ?? failure;
        if (fileWasAutoCreated) await DeleteEmptyRecordingAsync(file);
        await SyncStudioPreviewAsync();
        UpdateSessionActions();
        UpdateRecordingStatusLabels();
        FooterStatusText.Text = failure is null
            ? "Encoder mengakhiri rekaman sebelum Stop rekam ditekan. Periksa video di folder rekaman."
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
            _lastPrivateViewTick = _privateWindows.Count > 0 ? Environment.TickCount64 : 0;
            _capture.ConfigureDesktop(primaryDisplay.Bounds, primaryDisplay.WorkArea);
            ExcludePresenterFromScreenCapture();
            _capture.FrameHoldPredicate = () => _isAutoHeld || _isProtectedPublicCapture || HasPrivateView();
            _capture.StartMonitor(primaryDisplay.Handle, keepLastFrame: true);
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
        _isLive = false;
        _previewCaptureSuspended = false;
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
        RestorePresenterCaptureAffinity();

        _notesAudience = null;
        ApplyNoteCaptureRules();
        SetNotesEditingEnabled(true);

        UpdateCaptureModeUi();
    }

    private bool HasPrivateView()
    {
        var presenterExcluded = _presenterExcludedFromCapture
            && GetWindowDisplayAffinity(GetPresenterWindowHandle(), out var affinity) && affinity == ExcludeFromCapture;
        if (_presenterDisplay is null || WindowCatalogService.RequiresProtectedDesktop(_presenterDisplay.Bounds,
            _privateWindows, GetPresenterWindowHandle(), presenterExcluded, App.MainWindow is MainWindow { UsesPrivateThumbnail: true }))
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

        var protectedViewNeeded = HasPrivateView();
        if (!protectedViewNeeded && !_isProtectedPublicCapture && !_isAutoHeld && _privateWindows.Count == 0) return;
        var foregroundWindow = WindowCatalogService.GetForegroundWindowHandle();
        var presenterActive = foregroundWindow == GetPresenterWindowHandle();
        var privateWindowActive = WindowCatalogService.IsInWindowFamily(foregroundWindow, _privateWindows)
            && WindowCatalogService.IsWindowPresentable(foregroundWindow);
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
        if (protectedViewNeeded)
        {
            _capture.ProtectDesktop();
            if (!switcherActive) UpdateProtectedDesktop();
        }
        else if (wasProtected) _capture.ResumeDesktop();
        _isProtectedPublicCapture = protectedViewNeeded;
        _isAutoHeld = false;

        if (wasProtected != _isProtectedPublicCapture || wasHeld != _isAutoHeld)
            UpdatePresentationState();
    }

    private void UpdateProtectedDesktop()
    {
        if (_capture is null || _presenterDisplay is null) return;

        try
        {
            if (_lastPublicWindow != nint.Zero
                && !WindowCatalogService.IsInWindowFamily(_lastPublicWindow, _privateWindows))
                WindowCatalogService.PrepareWindowForBackgroundCapture(_lastPublicWindow);
            var publicWindows = WindowCatalogService.GetPublicDesktopWindows(_presenterDisplay.WorkArea, _privateWindows);
            _capture.SetProtectedWindows(publicWindows, retainSceneIfEmpty: true);
        }
        catch
        {
            // Keep the last good composition on transient enumeration/driver
            // failures. Never tear down a working scene during a private detour.
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
        _studioFramePending = true;
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

    private void OutputWindow_Closed(object sender, WindowEventArgs args)
    {
        _outputWindow = null;
        if (_recorder is not null || _sessionTransition)
        {
            _windowRescueTimer.Stop();
            _clientDisplay = null;
            _notesAudience = null;
            _isRecordingOnlySession = true;
            _recordingPreviewOpen = true;
            ApplyNoteCaptureRules();
            SessionModePicker.SelectedIndex = 0;
            FooterStatusText.Text = "Layar audiens ditutup. Klik Stop rekam jika ingin mengakhiri rekaman.";
            return;
        }
        StopPresentation();
    }

    private void PresenterWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_recoveryPending) return;
        if (_handoff) { args.Cancel = true; return; }
        if (_recorder is null && !_sessionTransition)
        {
            if (_backupOnClose && FreeNotesTextBox.Text.Length > 0)
            {
                args.Cancel = true;
                DispatcherQueue.TryEnqueue(async () => await FinishForUpdateAsync(false));
            }
            return;
        }
        args.Cancel = true;
        FooterStatusText.Text = _sessionTransition ? "Tunggu sampai rekaman selesai disiapkan atau disimpan."
            : "Rekaman masih berjalan. Klik Stop rekam dahulu untuk menutup Clario.";
    }

    private void LivePreviewCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args) => DrawStudioPreview(sender, args);

    private async void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _updateTimer.Stop();
        _updateTimer.Tick -= Updates_Tick;
        _updates.Changed -= ShowUpdateNotice;
        _updates.Pause();
        if (App.MainWindow is { } window)
        {
            window.AppWindow.Closing -= PresenterWindow_Closing;
            window.AppWindow.Changed -= StudioWindow_Changed;
        }
        await SyncStudioPreviewAsync();
        DisposeFloatingNotes();
        await StopRecordingAsync();
        StopPresentation();
        NoteReaderCanvas.RemoveFromVisualTree();
        _noteReaderRenderer.Dispose();
        LivePreviewCanvas.RemoveFromVisualTree();
    }

    private void UpdatePresentationState()
    {
        var state = ResolveState();
        _recorder?.SetAudioPrivacy(state == PresentationState.Privacy);
        var brush = state switch
        {
            PresentationState.Live => LiveBrush,
            PresentationState.Frozen => FreezeBrush,
            PresentationState.Privacy => ResourceBrush("ClarioPrivateStatusBrush"),
            _ => MutedBrush
        };

        FooterStatusIcon.Foreground = brush;
        UpdateSessionActions();
        UpdatePreview();
        _ = SyncStudioPreviewAsync();
        UpdateRecordingStatusLabels();

        switch (state)
        {
            case PresentationState.Live:
                _outputWindow?.SetMode(ClientOutputMode.Live);
                FooterStatusText.Text = _isProtectedPublicCapture
                    ? "Aplikasi yang disembunyikan tidak ikut tampil."
                    : _recorder?.IsRecording == true
                        ? "Sedang merekam. Klik Stop rekam untuk menyimpan video."
                        : _isRecordingOnlySession
                            ? "Preview aktif. Klik Mulai rekam untuk menyimpan video."
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
                    : "Tampilan ditahan. Klik Lanjutkan tampilan untuk memulihkan gambar.";
                break;
            case PresentationState.Privacy:
                _outputWindow?.SetMode(ClientOutputMode.Privacy);
                FooterStatusText.Text = _recorder?.IncludesAudio == true
                    ? "Tampilan disembunyikan dan audio dibisukan. Klik Matikan privasi untuk melanjutkan."
                    : "Tampilan disembunyikan. Klik Matikan privasi untuk menampilkan gambar.";
                break;
            default:
                FooterStatusText.Text = IsRecordingOnly
                    ? IsSafeMirrorMode
                        ? "Pilih layar, lalu klik Mulai rekam."
                        : "Pilih jendela aplikasi, lalu klik Mulai rekam."
                    : "Pilih sumber dan layar audiens, lalu klik Mulai presentasi.";
                break;
        }
    }

    private void UpdateSessionActions()
    {
        ShowUpdateNotice();
        var recording = _recorder is not null;
        PrimaryButton.IsEnabled = !_sessionTransition;
        PreviewButton.IsEnabled = !_sessionTransition;
        SettingsButton.IsEnabled = !_sessionTransition;
        RecordButton.IsEnabled = !_sessionTransition;
        SavedRecordingActions.Visibility = !_sessionTransition && !recording && _lastSavedRecording is not null
            ? Visibility.Visible : Visibility.Collapsed;
        PreviewButton.Visibility = IsRecordingOnly ? Visibility.Visible : Visibility.Collapsed;
        PreviewButton.Content = _isLive && !RecordingPreviewHidden ? "Tutup preview" : "Buka preview";
        SessionControls.Visibility = _isLive ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.Visibility = IsRecordingOnly ? Visibility.Collapsed : Visibility.Visible;
        FreezeButton.Content = _isFrozen ? "Lanjutkan tampilan" : "Freeze";
        PrivacyButton.Content = _isPrivacy ? "Matikan privasi" : "Privasi";
        if (_sessionTransition) return;

        PrimaryButton.Content = IsRecordingOnly
            ? recording ? "Stop rekam" : "Mulai rekam"
            : _isLive ? "Stop presentasi" : "Mulai presentasi";
        RecordButton.Content = recording ? "Stop rekam" : "Rekam";
    }

    private void UpdatePreview()
    {
        var frameVisible = _isLive && !_isPrivacy && !RecordingPreviewHidden && _capture?.HasFrame == true;
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
        if (RecordingPreviewHidden)
        {
            ClientStateTitle.Text = "Sedang merekam";
            ClientStateSubtitle.Text = "Klik Buka preview jika ingin memeriksa gambar dan suara.";
            return;
        }
        ClientStateTitle.Text = _isLive ? "Menyiapkan tampilan…" : IsRecordingOnly ? "Siap merekam" : "Siap presentasi";
        ClientStateSubtitle.Text = _isLive
            ? "Buka layar atau aplikasi yang ingin ditampilkan."
            : IsRecordingOnly
                ? "Klik Buka preview untuk memeriksa gambar, kamera, dan suara."
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
