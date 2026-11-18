using System.Runtime.InteropServices;

namespace Clario_Presenter.Audio;

internal readonly record struct AudioLevelReading(bool IsReady, int Peak, string? Error);

internal sealed class AudioLevelMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _peak;
    private volatile bool _isReady;
    private volatile string? _error;

    internal AudioLevelMonitor(string deviceId, bool loopback, bool speechProfile = false, bool warmNarration = false)
    {
        DeviceId = deviceId;
        SpeechProfile = speechProfile;
        WarmNarration = !loopback && warmNarration;
        _worker = Task.Run(() => ReadLevels(loopback, _stop.Token));
    }

    internal string DeviceId { get; }
    internal bool SpeechProfile { get; }
    internal bool WarmNarration { get; }
    internal bool HasStopped => _worker.IsCompleted;

    internal AudioLevelReading TakeReading() => new(_isReady, Interlocked.Exchange(ref _peak, 0), _error);

    private void ReadLevels(bool loopback, CancellationToken token)
    {
        short[] samples = [];
        var voice = WarmNarration ? new WarmVoiceProcessor() : null;
        try
        {
            // Use the same endpoint and PCM format as the recorder. The meter
            // keeps only the highest sample level since the previous UI tick.
            Wasapi.Capture(DeviceId, loopback, () => _isReady = true, (data, frames, flags, _, channels, _) =>
            {
                if (data == nint.Zero || (flags & Wasapi.Silent) != 0)
                {
                    voice?.Reset();
                    return;
                }
                if ((flags & Wasapi.Discontinuity) != 0) voice?.Reset();
                var count = checked(frames * channels);
                if (samples.Length < count) Array.Resize(ref samples, count);
                Marshal.Copy(data, samples, 0, count);
                voice?.Process(samples.AsSpan(0, count), channels);
                Accumulate(ref _peak, samples.AsSpan(0, count));
            }, token, SpeechProfile && !WarmNarration);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested) return;
            System.Diagnostics.Debug.WriteLine($"Audio meter: {exception.Message}");
            _error = loopback ? "Keluaran suara tidak bisa dibaca. Periksa perangkat yang dipilih."
                : Wasapi.MicrophoneError(exception);
        }
    }

    internal static void Accumulate(ref int accumulator, ReadOnlySpan<short> samples)
    {
        var peak = PeakOf(samples);
        int previous;
        do
        {
            previous = Volatile.Read(ref accumulator);
            if (peak <= previous) return;
        } while (Interlocked.CompareExchange(ref accumulator, peak, previous) != previous);
    }

    internal static int PeakOf(ReadOnlySpan<short> samples)
    {
        var peak = 0;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs((int)sample));
        return peak;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _worker.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
