using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Clario_Presenter.Settings;

public sealed class AppSettingsService
{
    private const string RecordingFolderName = "Clario Presenter";
    private readonly ApplicationDataContainer _localSettings = ApplicationData.Current.LocalSettings;

    public PresenterSettings Current { get; private set; }

    public AppSettingsService()
    {
        Current = Load();
    }

    public void Save(PresenterSettings settings)
    {
        Current = settings with
        {
            RecordingFrameRate = settings.RecordingFrameRate is 30 ? 30 : 60,
            RecordingBitrateMbps = Math.Clamp(settings.RecordingBitrateMbps, 8, 32)
        };

        _localSettings.Values[nameof(PresenterSettings.RecordingFrameRate)] = Current.RecordingFrameRate;
        _localSettings.Values[nameof(PresenterSettings.RecordingBitrateMbps)] = Current.RecordingBitrateMbps;
        _localSettings.Values[nameof(PresenterSettings.AskRecordingLocation)] = Current.AskRecordingLocation;
        _localSettings.Values[nameof(PresenterSettings.RecordingFolderPath)] = Current.RecordingFolderPath;
    }

    public async Task<StorageFolder> GetRecordingFolderAsync()
    {
        if (!string.IsNullOrWhiteSpace(Current.RecordingFolderPath))
        {
            try
            {
                return await StorageFolder.GetFolderFromPathAsync(Current.RecordingFolderPath);
            }
            catch
            {
                Save(Current with { RecordingFolderPath = string.Empty });
            }
        }

        var videosPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var videosFolder = await StorageFolder.GetFolderFromPathAsync(videosPath);
        var recordingFolder = await videosFolder.CreateFolderAsync(
            RecordingFolderName,
            CreationCollisionOption.OpenIfExists);

        Save(Current with { RecordingFolderPath = recordingFolder.Path });
        return recordingFolder;
    }

    public async Task<StorageFile> CreateRecordingFileAsync()
    {
        var folder = await GetRecordingFolderAsync();
        return await folder.CreateFileAsync(
            $"Clario-{DateTime.Now:yyyyMMdd-HHmmss}.mp4",
            CreationCollisionOption.GenerateUniqueName);
    }

    public static async Task<StorageFile?> PickRecordingFileAsync(nint ownerWindow)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            SuggestedFileName = $"Clario-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("Video MP4", [".mp4"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);
        return await picker.PickSaveFileAsync();
    }

    public static async Task<StorageFolder?> PickRecordingFolderAsync(nint ownerWindow)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);
        return await picker.PickSingleFolderAsync();
    }

    private PresenterSettings Load()
    {
        var values = _localSettings.Values;
        return new PresenterSettings
        {
            RecordingFrameRate = ReadInt(values, nameof(PresenterSettings.RecordingFrameRate), 60),
            RecordingBitrateMbps = ReadInt(values, nameof(PresenterSettings.RecordingBitrateMbps), 18),
            AskRecordingLocation = ReadBool(values, nameof(PresenterSettings.AskRecordingLocation)),
            RecordingFolderPath = values[nameof(PresenterSettings.RecordingFolderPath)] as string ?? string.Empty
        };
    }

    private static int ReadInt(IPropertySet values, string key, int fallback) =>
        values.TryGetValue(key, out var value) && value is int number ? number : fallback;

    private static bool ReadBool(IPropertySet values, string key) =>
        values.TryGetValue(key, out var value) && value is bool enabled && enabled;
}
