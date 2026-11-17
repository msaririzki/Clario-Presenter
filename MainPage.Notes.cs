using System.Diagnostics;
using System.Text;
using Clario_Presenter.Notes;
using Clario_Presenter.Services;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace Clario_Presenter;

public sealed partial class MainPage
{
    private FloatingNoteWindow? _floatingNotes;
    private DisplayTarget? _notesAudience;
    private bool _syncingNotesControls;
    private bool _importingNotes;
    private string? _notesFileName;
    private readonly DispatcherTimer _notesUpdateTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    private void InitializeFloatingNotes() => _notesUpdateTimer.Tick += NotesUpdateTimer_Tick;

    private void NoteOptions_Opening(object sender, object args)
    {
        var saved = (NotesMonitorComboBox.SelectedItem as DisplayTarget)?.DeviceName;
        _syncingNotesControls = true;
        try
        {
            var displays = DisplayCatalogService.GetDisplays();
            NotesMonitorComboBox.ItemsSource = displays;
            NotesMonitorComboBox.SelectedItem = displays.FirstOrDefault(display => display.DeviceName == saved)
                ?? displays.FirstOrDefault(display => display.DeviceName == DefaultNotesDisplay()?.DeviceName)
                ?? displays.FirstOrDefault();
        }
        finally { _syncingNotesControls = false; }
    }

    private DisplayTarget? DefaultNotesDisplay() =>
        _presenterDisplay ?? SourceMonitorComboBox.SelectedItem as DisplayTarget;

    private DisplayTarget? PreferredNotesDisplay() =>
        NotesMonitorComboBox.SelectedItem as DisplayTarget ?? DefaultNotesDisplay();

    private async void OpenNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_isLive || _importingNotes) return;
        _importingNotes = true;
        OpenNotesButton.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".md");
            picker.FileTypeFilter.Add(".markdown");
            picker.FileTypeFilter.Add(".txt");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, GetPresenterWindowHandle());
            var file = await picker.PickSingleFileAsync();
            if (file is null || _isLive) return;
            if ((await file.GetBasicPropertiesAsync()).Size > NoteDocument.MaximumLength * 4)
            {
                NoteStatusText.Text = "File catatan terlalu panjang. Gunakan maksimal 32.000 karakter.";
                return;
            }

            using var stream = await file.OpenStreamForReadAsync();
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var chars = new char[NoteDocument.MaximumLength + 1];
            var length = await reader.ReadBlockAsync(chars);
            if (_isLive) return;
            if (length > NoteDocument.MaximumLength)
            {
                NoteStatusText.Text = "File catatan terlalu panjang. Gunakan maksimal 32.000 karakter.";
                return;
            }
            _notesFileName = file.Name;
            _syncingNotesControls = true;
            try
            {
                FreeNotesTextBox.Text = new string(chars, 0, length);
                MarkdownNotesCheckBox.IsChecked = !file.FileType.Equals(".txt", StringComparison.OrdinalIgnoreCase);
            }
            finally { _syncingNotesControls = false; }
            NoteStatusText.Text = $"File dibuka: {_notesFileName}";
            RunNoteAction(() => UpdateNoteDocument(reset: true));
        }
        catch (DecoderFallbackException)
        {
            NoteStatusText.Text = "Simpan ulang file catatan sebagai UTF-8, lalu coba lagi.";
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            NoteStatusText.Text = "File catatan tidak bisa dibuka. Pilih file .md atau .txt.";
        }
        finally
        {
            _importingNotes = false;
            OpenNotesButton.IsEnabled = !_isLive;
        }
    }

    private void PinNotes_Click(object sender, RoutedEventArgs e) => RunNoteAction(() =>
    {
        if (_floatingNotes?.IsRequested == true)
        {
            _floatingNotes.Hide();
            return;
        }
        if (string.IsNullOrWhiteSpace(FreeNotesTextBox.Text))
        {
            NoteStatusText.Text = "Ketik catatan atau buka file terlebih dahulu.";
            return;
        }
        if (_floatingNotes is null)
        {
            _floatingNotes = new FloatingNoteWindow();
            _floatingNotes.Changed += FloatingNotes_Changed;
        }
        UpdateNoteDocument(reset: false);
        ApplyNoteAppearance();
        _floatingNotes.Toggle(PreferredNotesDisplay());
    });

    private void NoteAppearance_Changed(object sender, RoutedEventArgs args)
    {
        if (!_syncingNotesControls) RunNoteAction(ApplyNoteAppearance);
    }

    private void ApplyNoteAppearance()
    {
        if (_floatingNotes is null) return;
        _floatingNotes.SetAppearance((float)NotesOpacitySlider.Value / 100, (float)NotesFontSizeSlider.Value, (float)NotesWidthSlider.Value);
        _floatingNotes.SetClickThrough(NotesClickThroughToggle.IsOn);
        ApplyNoteCaptureRules();
    }

    private void ApplyNoteCaptureRules(DisplayTarget? recorded = null, DisplayTarget? audience = null)
    {
        RunNoteAction(() => _floatingNotes?.ApplyMonitors(PreferredNotesDisplay(),
            recorded ?? (_isSafeMirrorSession ? _presenterDisplay : null), audience ?? _notesAudience));
    }

    private void FloatingNotes_Changed(object? sender, EventArgs args)
    {
        if (_floatingNotes is null) return;
        PinNotesButton.Content = _floatingNotes.IsRequested ? "Tutup catatan" : "Sematkan";
        _syncingNotesControls = true;
        try { NotesClickThroughToggle.IsOn = _floatingNotes.ClickThrough; }
        finally { _syncingNotesControls = false; }
        NoteStatusText.Text = _floatingNotes.Status +
            (_floatingNotes.ShortcutsAvailable ? "" : " Sebagian shortcut tidak tersedia.");
    }

    private void FreeNotesTextBox_TextChanged(object sender, RoutedEventArgs args) => QueueNotesUpdate();
    private void MarkdownNotes_Changed(object sender, RoutedEventArgs args) => QueueNotesUpdate();

    private void QueueNotesUpdate()
    {
        if (_syncingNotesControls || _floatingNotes is null) return;
        _notesUpdateTimer.Stop();
        _notesUpdateTimer.Start();
    }

    private void NotesUpdateTimer_Tick(object? sender, object args)
    {
        _notesUpdateTimer.Stop();
        RunNoteAction(() => UpdateNoteDocument(reset: false));
    }

    private void UpdateNoteDocument(bool reset)
    {
        _floatingNotes?.SetDocument(NoteDocument.Parse(FreeNotesTextBox.Text, MarkdownNotesCheckBox.IsChecked == true), reset);
    }

    private void RunNoteAction(Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            _floatingNotes?.Hide();
            NoteStatusText.Text = "Catatan tidak dapat ditampilkan dengan aman. Coba sematkan lagi atau pilih layar lain.";
        }
    }

    // The editor is not the floating window. Hold the raw monitor capture if
    // the editor itself is visible on the source display, including behind a dialog.
    private bool IsNoteEditorOnRecordedDisplay() =>
        _presenterDisplay is not null && NotesPanel.Visibility == Visibility.Visible
        && !string.IsNullOrWhiteSpace(FreeNotesTextBox.Text)
        && WindowCatalogService.IsWindowPresentable(GetPresenterWindowHandle())
        && WindowCatalogService.HasPrivateWindowOnDisplay(_presenterDisplay.Bounds, [GetPresenterWindowHandle()]);

    private void SetNotesEditingEnabled(bool enabled)
    {
        OpenNotesButton.IsEnabled = enabled && !_importingNotes;
        FreeNotesTextBox.IsReadOnly = !enabled;
        MarkdownNotesCheckBox.IsEnabled = enabled;
    }

    private void DisposeFloatingNotes()
    {
        _notesUpdateTimer.Stop();
        _notesUpdateTimer.Tick -= NotesUpdateTimer_Tick;
        if (_floatingNotes is null) return;
        _floatingNotes.Changed -= FloatingNotes_Changed;
        _floatingNotes.Dispose();
        _floatingNotes = null;
    }
}
