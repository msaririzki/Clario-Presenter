namespace Clario_Presenter.Settings;

public sealed record PresenterSettings
{
    public int RecordingFrameRate { get; init; } = 60;
    public int RecordingBitrateMbps { get; init; } = 18;
    public bool AskRecordingLocation { get; init; }
    public string RecordingFolderPath { get; init; } = string.Empty;
}
