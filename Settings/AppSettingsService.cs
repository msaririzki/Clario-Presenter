using Clario_Presenter.Camera;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Pickers;
using System.Text.Json;

namespace Clario_Presenter.Settings;

public sealed class AppSettingsService
{
    private const string RecordingFolderName = "Clario Presenter";
#if CLARIO_UNPACKAGED
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clario Presenter", "settings.json");
#else
    private readonly ApplicationDataContainer _localSettings = ApplicationData.Current.LocalSettings;
#endif

    public PresenterSettings Current { get; private set; }

    public AppSettingsService()
    {
        Current = Load();
    }

    public void Save(PresenterSettings settings)
    {
        var normalized = Normalize(settings);
#if CLARIO_UNPACKAGED
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(SettingsPath)!, Path.GetRandomFileName());
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
        Current = normalized;
#else
        Current = normalized;
        _localSettings.Values[nameof(PresenterSettings.RecordingFrameRate)] = Current.RecordingFrameRate;
        _localSettings.Values[nameof(PresenterSettings.RecordingBitrateMbps)] = Current.RecordingBitrateMbps;
        _localSettings.Values[nameof(PresenterSettings.AskRecordingLocation)] = Current.AskRecordingLocation;
        _localSettings.Values[nameof(PresenterSettings.RecordingFolderPath)] = Current.RecordingFolderPath;
        _localSettings.Values[nameof(PresenterSettings.CameraEnabled)] = Current.CameraEnabled;
        _localSettings.Values[nameof(PresenterSettings.CameraDeviceId)] = Current.CameraDeviceId;
        _localSettings.Values[nameof(PresenterSettings.CameraPosition)] = Current.CameraPosition;
        _localSettings.Values[nameof(PresenterSettings.CameraWidthPercent)] = Current.CameraWidthPercent;
        _localSettings.Values[nameof(PresenterSettings.CameraMirror)] = Current.CameraMirror;
        _localSettings.Values[nameof(PresenterSettings.ComputerAudioEnabled)] = Current.ComputerAudioEnabled;
        _localSettings.Values[nameof(PresenterSettings.PlaybackDeviceId)] = Current.PlaybackDeviceId;
        _localSettings.Values[nameof(PresenterSettings.MicrophoneEnabled)] = Current.MicrophoneEnabled;
        _localSettings.Values[nameof(PresenterSettings.MicrophoneDeviceId)] = Current.MicrophoneDeviceId;
#endif
    }

    private static PresenterSettings Normalize(PresenterSettings settings) => settings with
    {
        RecordingFrameRate = settings.RecordingFrameRate is 30 ? 30 : 60,
        RecordingBitrateMbps = Math.Clamp(settings.RecordingBitrateMbps, 8, 32),
        CameraPosition = Math.Clamp(settings.CameraPosition, 0, 3),
        CameraWidthPercent = Math.Clamp(settings.CameraWidthPercent, CameraOverlayLayout.MinWidthPercent, CameraOverlayLayout.MaxWidthPercent)
    };

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
#if CLARIO_UNPACKAGED
        try
        {
            if (File.Exists(SettingsPath))
                return Normalize(JsonSerializer.Deserialize<PresenterSettings>(File.ReadAllText(SettingsPath)) ?? new());
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new();
#else
        var values = _localSettings.Values;
        return new PresenterSettings
        {
            RecordingFrameRate = ReadInt(values, nameof(PresenterSettings.RecordingFrameRate), 60),
            RecordingBitrateMbps = ReadInt(values, nameof(PresenterSettings.RecordingBitrateMbps), 18),
            AskRecordingLocation = ReadBool(values, nameof(PresenterSettings.AskRecordingLocation)),
            RecordingFolderPath = values[nameof(PresenterSettings.RecordingFolderPath)] as string ?? string.Empty,
            CameraEnabled = ReadBool(values, nameof(PresenterSettings.CameraEnabled)),
            CameraDeviceId = values[nameof(PresenterSettings.CameraDeviceId)] as string ?? string.Empty,
            CameraPosition = Math.Clamp(ReadInt(values, nameof(PresenterSettings.CameraPosition), 0), 0, 3),
            CameraWidthPercent = Math.Clamp(ReadInt(values, nameof(PresenterSettings.CameraWidthPercent), 24),
                CameraOverlayLayout.MinWidthPercent, CameraOverlayLayout.MaxWidthPercent),
            CameraMirror = ReadBool(values, nameof(PresenterSettings.CameraMirror)),
            ComputerAudioEnabled = ReadBool(values, nameof(PresenterSettings.ComputerAudioEnabled)),
            PlaybackDeviceId = values[nameof(PresenterSettings.PlaybackDeviceId)] as string ?? string.Empty,
            MicrophoneEnabled = ReadBool(values, nameof(PresenterSettings.MicrophoneEnabled)),
            MicrophoneDeviceId = values[nameof(PresenterSettings.MicrophoneDeviceId)] as string ?? string.Empty
        };
#endif
    }

    private static int ReadInt(IPropertySet values, string key, int fallback) =>
        values.TryGetValue(key, out var value) && value is int number ? number : fallback;

    private static bool ReadBool(IPropertySet values, string key) =>
        values.TryGetValue(key, out var value) && value is bool enabled && enabled;
}
