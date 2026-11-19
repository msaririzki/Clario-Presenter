using Clario_Presenter.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clario_Presenter;

public sealed partial class SettingsDialog
{
    private UpdateService? _updates;
    private Func<bool>? _updateAllowed;
    internal bool ApplyUpdate { get; private set; }
    private Action? _rememberManualUpdate;

    internal void ConfigureUpdates(UpdateService updates, Func<bool> allowed, Action rememberManualUpdate, bool select)
    {
        _updates = updates;
        _updateAllowed = allowed;
        _rememberManualUpdate = rememberManualUpdate;
        updates.Changed += UpdateDetails;
        InstalledVersion.Text = $"Versi terpasang {UpdateService.Installed}";
        if (select) SettingsNavigation.SelectedItem = UpdateNavigationItem;
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        if (_updates is null || _closing) return;
        bool allowed = _updateAllowed?.Invoke() == true;
        UpdateStatus.Text = allowed ? _updates.Message : "Selesaikan preview, rekaman, atau presentasi untuk memperbarui Clario.";
        ReleaseSummary.Text = _updates.Release?.Summary ?? "";
        ReleaseSummary.Visibility = ReleaseSummary.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdatesButton.IsEnabled = allowed && !_updates.Busy;
        UpdateActionButton.Visibility = _updates.Release is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateActionButton.IsEnabled = allowed && !_updates.Busy;
        UpdateActionButton.Content = _updates.ReadyPath is null ? "Unduh pembaruan" : "Pasang dan buka kembali";
        ReleaseNotesLink.NavigateUri = _updates.Release?.Page ?? new Uri(UpdateRelease.Repository + "/releases");
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updateAllowed?.Invoke() == true && _updates is not null) await _updates.CheckAsync(true);
    }

    private async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_updateAllowed?.Invoke() != true || _updates is not { Busy: false, Release: { } release }) return;
        if (_updates.ReadyPath is null)
        {
            try
            {
                _rememberManualUpdate?.Invoke();
                if (!await Windows.System.Launcher.LaunchUriAsync(release.Page)) UpdateStatus.Text = "Halaman unduhan belum bisa dibuka.";
            }
            catch { UpdateStatus.Text = "Halaman unduhan belum bisa dibuka."; }
            return;
        }
        ApplyUpdate = true;
        SaveSettings_Click(sender, e);
        if (!IsSaved) ApplyUpdate = false;
    }
}
