using Clario_Presenter.Audio;
using Clario_Presenter.Camera;

namespace Clario_Presenter.Settings;

public sealed record PresenterSettings
{
    public int RecordingFrameRate { get; init; } = 60;
    public int RecordingBitrateMbps { get; init; } = 18;
    public bool AskRecordingLocation { get; init; }
    public string RecordingFolderPath { get; init; } = string.Empty;
    public bool CameraEnabled { get; init; }
    public string CameraDeviceId { get; init; } = string.Empty;
    public int CameraPosition { get; init; }
    public CameraOverlayPoint CameraCustomPosition { get; init; } = new(1, 1);
    public bool CameraCustomPositionUsesFullFrame { get; init; }
    public int CameraWidthPercent { get; init; } = 24;
    public bool CameraMirror { get; init; }
    public bool ComputerAudioEnabled { get; init; }
    public string PlaybackDeviceId { get; init; } = string.Empty;
    public bool MicrophoneEnabled { get; init; }
    public string MicrophoneDeviceId { get; init; } = Wasapi.AutomaticInputId;
}
