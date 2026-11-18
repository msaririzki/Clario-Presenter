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

    internal AudioLevelMonitor(string deviceId, bool loopback, bool speechProfile = false, bool warmNarration = false, bool reduceFanNoise = false,
        string[]? boostedMicrophones = null)
    {
        DeviceId = deviceId;
        BoostedMicrophones = loopback ? [] : boostedMicrophones ?? [];
        SpeechProfile = speechProfile;
        WarmNarration = !loopback && warmNarration;
        ReduceFanNoise = !loopback && reduceFanNoise && FanNoiseReducer.Supported;
        _worker = Task.Run(() => ReadLevels(loopback, _stop.Token));
    }

    internal string[] BoostedMicrophones { get; }
    internal string DeviceId { get; }
    internal bool SpeechProfile { get; }
    internal bool WarmNarration { get; }
    internal bool ReduceFanNoise { get; }
    internal bool HasStopped => _worker.IsCompleted;

    internal AudioLevelReading TakeReading() => new(_isReady, Interlocked.Exchange(ref _peak, 0), _error);

    private void ReadLevels(bool loopback, CancellationToken token)
    {
        short[] samples = [];
        QuietMicBoost? boost = null;
        var voice = WarmNarration ? new WarmVoiceProcessor() : null;
        using var noise = ReduceFanNoise ? new FanNoiseReducer() : null;
        try
        {
            // Use the same endpoint and PCM format as the recorder. The meter
            // keeps only the highest sample level since the previous UI tick.
            Wasapi.Capture(DeviceId, loopback, () => _isReady = true, (data, frames, flags, _, channels, _) =>
            {
                if ((flags & Wasapi.Discontinuity) != 0)
                {
                    boost?.Reset();
                    voice?.Reset();
                    noise?.Reset();
                }
                var count = checked(frames * channels);
                if (samples.Length < count) Array.Resize(ref samples, count);
                if (data == nint.Zero || (flags & Wasapi.Silent) != 0)
                {
                    boost?.Reset();
                    count = noise?.Drain(samples.AsSpan(0, count), channels) ?? 0;
                    if (count == 0) voice?.Reset();
                }
                else
                {
                    Marshal.Copy(data, samples, 0, count);
                    boost?.Process(samples.AsSpan(0, count), channels);
                    noise?.Process(samples.AsSpan(0, count), channels);
                }
                voice?.Process(samples.AsSpan(0, count), channels);
                Accumulate(ref _peak, samples.AsSpan(0, count), !loopback);
            }, token, SpeechProfile && !WarmNarration, BoostedMicrophones.Length == 0 ? null
                : endpoint => boost = QuietMicBoost.ForEndpoint(endpoint, BoostedMicrophones));
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

    internal static void Accumulate(ref int accumulator, ReadOnlySpan<short> samples, bool microphone = false)
    {
        var peak = PeakOf(samples);
        if (microphone && peak < 29204 && samples.Length > 0) // retain near-clipping warnings
        {
            long energy = 0;
            foreach (var sample in samples) energy += sample * sample;
            peak = (int)Math.Sqrt((double)energy / samples.Length);
            if (peak < 65) peak = 0; // below -54 dBFS RMS: idle on the meter
        }
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
