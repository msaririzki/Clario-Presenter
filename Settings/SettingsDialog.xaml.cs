using Clario_Presenter.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.System;

namespace Clario_Presenter;

public sealed partial class SettingsDialog : ContentDialog
{
    private string _folderPath;

    public SettingsDialog(PresenterSettings settings)
    {
        InitializeComponent();
        _folderPath = settings.RecordingFolderPath;
        FrameRateComboBox.SelectedIndex = settings.RecordingFrameRate == 30 ? 1 : 0;
        BitrateComboBox.SelectedIndex = settings.RecordingBitrateMbps switch
        {
            <= 10 => 0,
            >= 28 => 2,
            _ => 1
        };
        AskLocationToggle.IsOn = settings.AskRecordingLocation;
        UpdateFolderLabel();
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

        return new PresenterSettings
        {
            RecordingFrameRate = frameRate,
            RecordingBitrateMbps = bitrate,
            AskRecordingLocation = AskLocationToggle.IsOn,
            RecordingFolderPath = _folderPath
        };
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
            ? "Videos\\Clario Presenter (default)"
            : _folderPath;
    }
}
