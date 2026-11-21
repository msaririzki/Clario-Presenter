using Clario_Presenter.Notes;
using Clario_Presenter.Services;
using Clario_Presenter.Updates;
using Microsoft.UI.Xaml;

namespace Clario_Presenter;

public sealed partial class MainPage
{
    private readonly UpdateService _updates = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _handoff, _recoveryPending, _backupOnClose;
    private bool UpdatesAllowed => _isLoaded && !_isLive && _recorder is null && !_sessionTransition && !_handoff && !_importingNotes;

    private async void Updates_Tick(object? sender, object args)
    {
        _updateTimer.Interval = TimeSpan.FromMinutes(1);
        if (UpdatesAllowed && UpdateService.InstalledCopy) await _updates.CheckAsync(false);
    }

    private void ShowUpdateNotice()
    {
        if (UpdateNotice is null) return;
        UpdateNotice.Visibility = UpdatesAllowed && !_updates.Deferred && !_updates.Busy && _updates.Release is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs args) => _updates.Later();

    private UpdateNotes CurrentNotes() => new(FreeNotesTextBox.Text, _notesFileName,
        MarkdownNotesCheckBox.IsChecked == true, NoteReader.Visibility == Visibility.Visible,
        NotesPanel.Visibility == Visibility.Visible, _noteReference is null && _floatingNotes?.IsRequested == true,
        FreeNotesTextBox.SelectionStart, NoteReaderScroll.VerticalOffset, _floatingNotes?.ReadingOffset ?? 0,
        NotesFontSizeSlider.Value, NotesWidthSlider.Value, NotesHeightSlider.Value, NotesOpacitySlider.Value,
        NotesClickThroughToggle.IsOn, PreferredNotesDisplay()?.DeviceName);

    private async Task RestoreUpdateNotesAsync()
    {
        if (!UpdateService.InstalledCopy || !File.Exists(UpdateNotes.PathOnDisk)) return;
        _handoff = true;
        IsEnabled = false;
        try
        {
            var notes = await UpdateNotes.ReadAsync();
            if (notes.Text.Length > NoteDocument.MaximumLength) throw new InvalidDataException();
            _notesFileName = notes.Name;
            FreeNotesTextBox.Text = notes.Text;
            MarkdownNotesCheckBox.IsChecked = notes.Markdown;
            SetNoteReader(notes.Reader && notes.Markdown);
            FreeNotesTextBox.SelectionStart = Math.Clamp(notes.Caret, 0, notes.Text.Length);
            NotesButton.IsChecked = notes.Panel;
            SetNotesVisible(notes.Panel);
            NotesFontSizeSlider.Value = notes.FontSize;
            NotesWidthSlider.Value = notes.Width;
            NotesHeightSlider.Value = notes.Height;
            NotesOpacitySlider.Value = notes.Opacity;
            NotesClickThroughToggle.IsOn = notes.ClickThrough;
            var displays = DisplayCatalogService.GetDisplays();
            NotesMonitorComboBox.ItemsSource = displays;
            NotesMonitorComboBox.SelectedItem = displays.FirstOrDefault(d => d.DeviceName == notes.Display) ?? DefaultNotesDisplay();
            if (notes.Pinned)
            {
                _floatingNotes = new FloatingNoteWindow();
                _floatingNotes.Changed += FloatingNotes_Changed;
            }
            UpdateNoteDocument(false);
            if (notes.Pinned)
            {
                ApplyNoteAppearance();
                _floatingNotes!.Show(PreferredNotesDisplay());
                _floatingNotes.RestoreReadingOffset(notes.FloatingOffset);
            }
            _restoreReaderOffset = notes.ReaderOffset;
            NoteReaderCanvas.Invalidate();
            FooterStatusText.Text = "Catatan dipulihkan setelah pembaruan.";
            File.Delete(UpdateNotes.PathOnDisk);
        }
        catch { FooterStatusText.Text = "Catatan pembaruan belum bisa dipulihkan. Salinan pemulihan masih tersimpan."; }
        finally { _handoff = false; IsEnabled = true; }
    }

    private double? _restoreReaderOffset;

    private void ShowInstallationResult()
    {
        if (!UpdateService.InstalledCopy) return;
        var path = Path.Combine(UpdateService.Folder, "last-install.txt");
        try
        {
            if (!File.Exists(path)) return;
            var target = new FileInfo(path).Length <= 64 && Version.TryParse(File.ReadAllText(path), out var value) ? value : null;
            File.Delete(path);
            if (target is null) return;
            var message = UpdateService.Installed >= target
                ? $"Clario sudah diperbarui ke {UpdateService.Installed}."
                : $"Pembaruan ke {target} belum berhasil. Versi terpasang masih {UpdateService.Installed}.";
            FooterStatusText.Text = message + (FooterStatusText.Text.StartsWith("Catatan ", StringComparison.Ordinal) ? " " + FooterStatusText.Text : "");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task FinishForUpdateAsync(bool install)
    {
        if (install ? !UpdatesAllowed : _handoff || _recorder is not null || _sessionTransition || _importingNotes) return;
        _handoff = true;
        IsEnabled = false;
        bool saved = false;
        try
        {
            if (HasOtherInstalledInstance()) throw new InvalidOperationException("Tutup jendela Clario lain sebelum memperbarui.");
            if (FreeNotesTextBox.Text.Length > 0)
            {
                await CurrentNotes().SaveAsync();
                saved = true;
            }
            if (install) await _updates.StartInstallationAsync();
            _backupOnClose = false;
            _recoveryPending = true;
            App.MainWindow?.Close();
        }
        catch (Exception error)
        {
            if (saved) { try { File.Delete(UpdateNotes.PathOnDisk); } catch (IOException) { } }
            FooterStatusText.Text = $"Clario tetap terbuka. {error.Message}";
        }
        finally { _handoff = false; IsEnabled = true; }
    }

    private static bool HasOtherInstalledInstance()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("Clario.Presenter"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try { if (string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return true; }
                catch (InvalidOperationException) { }
            }
        }
        return false;
    }
}
