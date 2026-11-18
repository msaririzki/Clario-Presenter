using Clario_Presenter.Audio;
using Clario_Presenter.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clario_Presenter;

public sealed partial class SettingsDialog
{
    private bool _loadingAudioDevices;
    private bool _loadedAudioDevices;
    private bool AudioEnabled => ComputerAudioToggle.IsOn || MicrophoneAudioToggle.IsOn;

    private void InitializeAudioSettings(PresenterSettings settings)
    {
        ComputerAudioToggle.IsOn = settings.ComputerAudioEnabled;
        MicrophoneAudioToggle.IsOn = settings.MicrophoneEnabled;
        UpdateAudioOptions();
    }

    private async void AudioSource_Changed(object sender, RoutedEventArgs args)
    {
        if (!_opened || _closing) return;
        SettingsErrorText.Visibility = Visibility.Collapsed;
        UpdateAudioOptions();
        if (AudioEnabled && !_loadedAudioDevices) await RefreshAudioDevicesAsync();
    }

    private void UpdateAudioOptions()
    {
        PlaybackAudioComboBox.Visibility = ComputerAudioToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneAudioComboBox.Visibility = MicrophoneAudioToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        AudioDeviceActions.Visibility = AudioEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshAudio_Click(object sender, RoutedEventArgs args) => await RefreshAudioDevicesAsync();

    private async Task RefreshAudioDevicesAsync()
    {
        if (_loadingAudioDevices) return;
        _loadingAudioDevices = true;
        RefreshAudioButton.IsEnabled = false;
        AudioStatusText.Text = "Memuat perangkat audio…";
        try
        {
            var devices = await Task.Run(Wasapi.Enumerate);
            if (_closing) return;
            LoadAudioOptions(PlaybackAudioComboBox, devices.Outputs, _originalSettings.PlaybackDeviceId);
            LoadAudioOptions(MicrophoneAudioComboBox, devices.Inputs, _originalSettings.MicrophoneDeviceId);
            _loadedAudioDevices = true;
            _loadingAudioDevices = false;
            AudioStatusText.Text = GetAudioSelectionError() ?? string.Empty;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            if (!_closing) AudioStatusText.Text = "Daftar perangkat belum bisa dimuat. Klik Perbarui perangkat audio untuk mencoba lagi.";
        }
        finally
        {
            _loadingAudioDevices = false;
            if (!_closing) RefreshAudioButton.IsEnabled = true;
        }
    }

    private void AudioDevice_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_opened || _loadingAudioDevices || _closing) return;
        AudioStatusText.Text = GetAudioSelectionError() ?? string.Empty;
        SettingsErrorText.Visibility = Visibility.Collapsed;
    }

    private static void LoadAudioOptions(ComboBox picker, IReadOnlyList<AudioDevice> devices, string savedId)
    {
        var id = SelectedAudioDeviceId(picker, savedId);
        var options = new List<AudioDevice>(devices.Count + 2)
        {
            new(string.Empty, "Bawaan Windows", devices.Count > 0)
        };
        options.AddRange(devices);
        var selected = options.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            selected = new(id, "Perangkat tersimpan belum terhubung", false);
            options.Add(selected);
        }
        picker.ItemsSource = options;
        picker.SelectedItem = selected;
    }

    private string? GetAudioSelectionError()
    {
        if (!AudioEnabled) return null;
        if (_loadingAudioDevices) return "Daftar perangkat audio sedang dimuat.";
        if (ComputerAudioToggle.IsOn && PlaybackAudioComboBox.SelectedItem is not AudioDevice { Available: true })
            return "Pilih keluaran suara yang terhubung untuk merekam suara komputer.";
        if (MicrophoneAudioToggle.IsOn && MicrophoneAudioComboBox.SelectedItem is not AudioDevice { Available: true })
            return "Pilih mikrofon yang terhubung atau perbarui daftar perangkat audio.";
        return null;
    }

    private static string SelectedAudioDeviceId(ComboBox picker, string savedId) =>
        (picker.SelectedItem as AudioDevice)?.Id ?? savedId;
}
