using Clario_Presenter.Audio;
using Clario_Presenter.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Clario_Presenter;

public sealed partial class SettingsDialog
{
    private readonly HashSet<string> _boostedMicrophones = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingBoostToggle;
    private bool _loadingAudioDevices;
    private bool _loadedAudioDevices;
    private readonly SemaphoreSlim _audioMonitorGate = new(1, 1);
    private readonly DispatcherTimer _audioMeterTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private AudioLevelMonitor? _playbackMonitor;
    private AudioLevelMonitor? _microphoneMonitor;
    private bool AudioEnabled => ComputerAudioToggle.IsOn || MicrophoneAudioToggle.IsOn;

    private void InitializeAudioSettings(PresenterSettings settings)
    {
        _boostedMicrophones.UnionWith(settings.BoostedMicrophones);
        ComputerAudioToggle.IsOn = settings.ComputerAudioEnabled;
        MicrophoneAudioToggle.IsOn = settings.MicrophoneEnabled;
        FanNoiseToggle.IsOn = settings.ReduceFanNoise && FanNoiseReducer.Supported;
        FanNoiseToggle.Visibility = FanNoiseReducer.Supported ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneProfilePicker.SelectedIndex = settings.WarmNarration ? 2 : settings.MicrophoneSpeechProfile ? 0 : 1;
        UpdateAudioOptions();
        _audioMeterTimer.Tick += AudioMeterTimer_Tick;
    }

    private async void AudioSource_Changed(object sender, RoutedEventArgs args)
    {
        if (!_opened || _closing) return;
        SettingsErrorText.Visibility = Visibility.Collapsed;
        UpdateAudioOptions();
        if (AudioEnabled && !_loadedAudioDevices) await RefreshAudioDevicesAsync();
        else await SyncAudioMonitorsAsync();
    }

    private void UpdateAudioOptions()
    {
        PlaybackAudioOptions.Visibility = ComputerAudioToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneAudioOptions.Visibility = MicrophoneAudioToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
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
            LoadAudioOptions(PlaybackAudioComboBox, devices.Outputs, devices.DefaultOutput, _originalSettings.PlaybackDeviceId);
            LoadAudioOptions(MicrophoneAudioComboBox, devices.Inputs, devices.DefaultInput, _originalSettings.MicrophoneDeviceId, devices.AutomaticInput);
            ShowSelectedMicBoost();
            _loadedAudioDevices = true;
            _loadingAudioDevices = false;
            AudioStatusText.Text = GetAudioStatus();
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
        if (!_closing) await SyncAudioMonitorsAsync();
    }

    private async void AudioDevice_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_opened || _loadingAudioDevices || _closing) return;
        if (ReferenceEquals(sender, MicrophoneAudioComboBox)) ShowSelectedMicBoost();
        AudioStatusText.Text = GetAudioStatus();
        SettingsErrorText.Visibility = Visibility.Collapsed;
        await SyncAudioMonitorsAsync();
    }

    private void ShowSelectedMicBoost()
    {
        var mic = MicrophoneAudioComboBox.SelectedItem as AudioDevice;
        _syncingBoostToggle = true;
        MicBoostToggle.IsEnabled = mic?.Available == true;
        MicBoostToggle.IsOn = mic?.Available == true && _boostedMicrophones.Contains(mic.EndpointId);
        _syncingBoostToggle = false;
    }

    private async void MicBoost_Toggled(object sender, RoutedEventArgs args)
    {
        if (_syncingBoostToggle || !_opened || _closing ||
            MicrophoneAudioComboBox.SelectedItem is not AudioDevice { Available: true } mic) return;
        if (MicBoostToggle.IsOn) _boostedMicrophones.Add(mic.EndpointId);
        else _boostedMicrophones.Remove(mic.EndpointId);
        await SyncAudioMonitorsAsync();
    }

    private async Task SyncAudioMonitorsAsync()
    {
        await _audioMonitorGate.WaitAsync();
        try
        {
            _playbackMonitor = await SyncAudioMonitorAsync(_playbackMonitor, ComputerAudioToggle, PlaybackAudioComboBox,
                PlaybackLevelBar, loopback: true);
            _microphoneMonitor = await SyncAudioMonitorAsync(_microphoneMonitor, MicrophoneAudioToggle, MicrophoneAudioComboBox,
                MicrophoneLevelBar, loopback: false);
            if (_closing || AudioSection.Visibility != Visibility.Visible)
            {
                _audioMeterTimer.Stop();
                return;
            }

            UpdateAudioMeters();
            if (_playbackMonitor is not null || _microphoneMonitor is not null) _audioMeterTimer.Start();
            else _audioMeterTimer.Stop();
        }
        finally { _audioMonitorGate.Release(); }
    }

    private async Task<AudioLevelMonitor?> SyncAudioMonitorAsync(AudioLevelMonitor? monitor,
        ToggleSwitch toggle, ComboBox picker, ProgressBar bar, bool loopback)
    {
        var desired = DesiredAudioDevice(toggle, picker);
        var speech = !loopback && MicrophoneProfilePicker.SelectedIndex != 1;
        var warm = !loopback && MicrophoneProfilePicker.SelectedIndex == 2;
        if (monitor is { HasStopped: false } && desired?.EndpointId == monitor.DeviceId
            && (loopback || monitor.BoostedMicrophones.Contains(monitor.DeviceId, StringComparer.OrdinalIgnoreCase) == MicBoostToggle.IsOn)
            && monitor.SpeechProfile == speech && monitor.WarmNarration == warm
            && monitor.ReduceFanNoise == (!loopback && FanNoiseToggle.IsOn)) return monitor;
        if (monitor is not null) await monitor.DisposeAsync();
        bar.Value = 0;
        return DesiredAudioDevice(toggle, picker) is { } next
            ? new AudioLevelMonitor(next.EndpointId, loopback, !loopback && MicrophoneProfilePicker.SelectedIndex != 1,
                !loopback && MicrophoneProfilePicker.SelectedIndex == 2, !loopback && FanNoiseToggle.IsOn,
                !loopback && MicBoostToggle.IsOn ? [next.EndpointId] : null) : null;
    }

    private AudioDevice? DesiredAudioDevice(ToggleSwitch toggle, ComboBox picker) =>
        !_closing && AudioSection.Visibility == Visibility.Visible && toggle.IsOn
            && picker.SelectedItem is AudioDevice { Available: true } device ? device : null;

    private void AudioMeterTimer_Tick(object? sender, object args)
    {
        if (!_closing && AudioSection.Visibility == Visibility.Visible) UpdateAudioMeters();
    }

    private void UpdateAudioMeters()
    {
        UpdateAudioMeter(_playbackMonitor, PlaybackLevelBar, PlaybackLevelStatus);
        UpdateAudioMeter(_microphoneMonitor, MicrophoneLevelBar, MicrophoneLevelStatus);
    }

    private static void UpdateAudioMeter(AudioLevelMonitor? monitor, ProgressBar bar, TextBlock status)
    {
        var reading = monitor?.TakeReading() ?? default;
        if (monitor is null || reading.Error is not null || !reading.IsReady)
        {
            bar.Value = 0;
            status.Text = reading.Error ?? (monitor is null ? "Pilih perangkat untuk memeriksa sinyal." : "Memeriksa perangkat…");
            return;
        }

        var db = reading.Peak == 0 ? -60 : Math.Max(-60, 20 * Math.Log10(reading.Peak / 32768d));
        bar.Value = Math.Max((db + 60) / 60 * 100, bar.Value - 14);
        var displayedDb = -60 + bar.Value * 60 / 100;
        var brush = displayedDb >= -1 ? "ClarioPrivacyBrush" : displayedDb >= -6 ? "ClarioFreezeBrush" : "ClarioLiveBrush";
        bar.Foreground = (Brush)Application.Current.Resources[brush];
        status.Text = bar.Value == 0 ? "Siap · belum ada sinyal"
            : displayedDb >= -1 ? $"Sinyal terlalu tinggi · {displayedDb:0} dBFS"
            : $"Sinyal terdeteksi · {displayedDb:0} dBFS";
    }

    private void LoadAudioOptions(ComboBox picker, IReadOnlyList<AudioDevice> devices, AudioDevice? windowsDefault,
        string savedId, AudioDevice? automatic = null)
    {
        var id = SelectedAudioDeviceId(picker, savedId);
        var options = new List<AudioDevice>(devices.Count + 3);
        if (ReferenceEquals(picker, MicrophoneAudioComboBox))
            options.Add(DeviceOption(Wasapi.AutomaticInputId, "Otomatis", automatic));
        options.Add(DeviceOption(string.Empty, "Bawaan Windows", windowsDefault));
        options.AddRange(devices.Select(device => device.Origin == AudioInputOrigin.Integrated
            ? device with { Name = $"{device.Name} · bawaan perangkat" }
            : device));
        var selected = options.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            selected = new(id, "Perangkat tersimpan belum terhubung", false);
            options.Add(selected);
        }
        picker.ItemsSource = options;
        picker.SelectedItem = selected;
    }

    private static AudioDevice DeviceOption(string id, string label, AudioDevice? endpoint) =>
        endpoint is null ? new(id, $"{label} tidak tersedia", false)
            : endpoint with { Id = id, Name = $"{label} · {endpoint.Name}", ResolvedId = endpoint.Id };

    private string GetAudioStatus()
    {
        if (GetAudioSelectionError() is { } error) return error;
        if (MicrophoneAudioToggle.IsOn && MicrophoneAudioComboBox.SelectedItem is AudioDevice
            { Id.Length: 0, Origin: AudioInputOrigin.Software } selected)
        {
            var automatic = MicrophoneAudioComboBox.Items.OfType<AudioDevice>()
                .FirstOrDefault(device => device.Id == Wasapi.AutomaticInputId && device.Available);
            if (automatic is not null && automatic.EndpointId != selected.EndpointId)
                return "Windows memilih mikrofon virtual. Pilih Otomatis untuk memakai mikrofon bawaan perangkat.";
        }
        return string.Empty;
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
