using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Clario_Presenter.Audio;

public sealed record RecordingAudioOptions(
    bool ComputerAudioEnabled = false, string PlaybackDeviceId = "",
    bool MicrophoneEnabled = false, string MicrophoneDeviceId = "", bool MicrophoneSpeechProfile = true,
    bool WarmNarration = false)
{
    public bool Enabled => ComputerAudioEnabled || MicrophoneEnabled;
}

internal static class RecordingAudioFormat
{
    internal const int SampleRate = 48_000;
    internal const int Channels = 2;
    internal const int PacketFrames = 960;

    internal static long FramesAt(TimeSpan elapsed) => (long)(elapsed.TotalSeconds * SampleRate);
    internal static TimeSpan PacketTime(long frames) => TimeSpan.FromTicks(frames * TimeSpan.TicksPerSecond / SampleRate);
}

internal interface IRecordingAudioSource
{
    Task StartAsync(CancellationToken token);
    void StartClock(long timestamp);
    void Read(long firstFrame, Span<short> samples);
    string? Warning { get; }
    Task Completion { get; }
}

internal sealed class RecordingAudioSource(RecordingAudioOptions options, Func<bool> muted) : IRecordingAudioSource
{
    private CaptureTrack[] _tracks = [];
    private long _origin;
    public string? Warning => string.Join(" ", _tracks.Select(track => track.Warning).OfType<string>()) is { Length: > 0 } warning
        ? warning : null;
    public Task Completion { get; private set; } = Task.CompletedTask;

    internal volatile bool MeteringEnabled;
    internal AudioLevelReading? ReadLevel(bool loopback)
    {
        var track = Array.Find(_tracks, item => item.Loopback == loopback);
        if (track is null) return null;
        return new(track.Started.Task.IsCompletedSuccessfully, Interlocked.Exchange(ref track.Peak, 0), track.Warning);
    }

    public Task StartAsync(CancellationToken token)
    {
        var inputs = new List<CaptureTrack>(2);
        if (options.ComputerAudioEnabled) inputs.Add(new(options.PlaybackDeviceId, true));
        if (options.MicrophoneEnabled) inputs.Add(new(options.MicrophoneDeviceId, false));
        _tracks = inputs.ToArray();
        var workers = _tracks.Select(track => Task.Run(() => Capture(track, token))).ToArray();
        Completion = Task.WhenAll(workers);
        return Task.WhenAll(_tracks.Select(track => track.Started.Task));
    }

    public void StartClock(long timestamp) => Volatile.Write(ref _origin, timestamp);

    public void Read(long firstFrame, Span<short> samples)
    {
        if (_tracks.Length == 0) return;
        _tracks[0].Timeline.Read(firstFrame, samples);
        if (_tracks.Length == 1) return;
        _tracks[1].Timeline.Mix(firstFrame, samples);
    }

    private void Capture(CaptureTrack track, CancellationToken token)
    {
        short[] pcm = [];
        var clock = new CapturePacketClock();
        var voice = !track.Loopback && options.WarmNarration ? new WarmVoiceProcessor() : null;
        try
        {
            Wasapi.Capture(track.DeviceId, track.Loopback, () => track.Started.TrySetResult(),
                (data, frames, flags, timestamp, channels, position) =>
                {
                    var origin = Volatile.Read(ref _origin);
                    if (origin == 0) return;
                    long? timestampFrame = timestamp == 0 ? null
                        : (long)Math.Round((timestamp - origin * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)
                            * RecordingAudioFormat.SampleRate / TimeSpan.TicksPerSecond);
                    var firstFrame = clock.Place(frames, position, flags, timestampFrame,
                        RecordingAudioFormat.FramesAt(Stopwatch.GetElapsedTime(origin)));
                    if (muted() || data == 0 || (flags & Wasapi.Silent) != 0)
                    {
                        voice?.Reset();
                        return;
                    }
                    if ((flags & Wasapi.Discontinuity) != 0) voice?.Reset();
                    var count = frames * channels;
                    if (pcm.Length < count) Array.Resize(ref pcm, count);
                    Marshal.Copy(data, pcm, 0, count);
                    voice?.Process(pcm.AsSpan(0, count), channels);
                    if (MeteringEnabled) AudioLevelMonitor.Accumulate(ref track.Peak, pcm.AsSpan(0, count));
                    track.Timeline.Store(firstFrame, pcm.AsSpan(0, count), channels);
                }, token, options.MicrophoneSpeechProfile);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            track.Started.TrySetCanceled(token);
        }
        catch (Exception exception)
        {
            var source = track.Loopback ? "Suara komputer" : "Mikrofon";
            if (!track.Started.TrySetException(new InvalidOperationException(
                    track.Loopback ? $"{source} tidak bisa dibuka. Periksa perangkat keluaran di Pengaturan."
                    : Wasapi.MicrophoneError(exception), exception))
                && !token.IsCancellationRequested)
                track.Warning = $"{source} terputus.";
        }
    }

    private sealed class CaptureTrack(string deviceId, bool loopback)
    {
        internal int Peak;
        internal string DeviceId { get; } = deviceId;
        internal bool Loopback { get; } = loopback;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal PcmTimeline Timeline { get; } = new();
        internal volatile string? Warning;
    }
}
