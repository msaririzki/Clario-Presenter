namespace Clario_Presenter.Audio;

/// <summary>Retains recent PCM by recording frame so gaps are silent and never delay the video.</summary>
internal sealed class PcmTimeline
{
    private const int Capacity = RecordingAudioFormat.SampleRate * 2;
    private readonly object _gate = new();
    private readonly long[] _timestamps = new long[Capacity];
    private readonly short[] _samples = new short[Capacity * 2];

    internal void Store(long firstFrame, ReadOnlySpan<short> samples, int channels)
    {
        lock (_gate)
        {
            var count = samples.Length / channels;
            var skip = (int)Math.Min(count, Math.Max(0, Math.Max(-firstFrame, count - Capacity)));
            for (var index = skip; index < count; index++)
            {
                var frame = firstFrame + index;
                var slot = (int)(frame % Capacity);
                _samples[slot * 2] = samples[index * channels];
                _samples[slot * 2 + 1] = samples[index * channels + channels - 1];
                _timestamps[slot] = frame + 1;
            }
        }
    }

    internal void Read(long firstFrame, Span<short> destination)
    {
        lock (_gate)
        {
            for (var index = 0; index < destination.Length; index += 2)
            {
                var frame = firstFrame + index / 2;
                var slot = (int)(frame % Capacity);
                var found = _timestamps[slot] == frame + 1;
                destination[index] = found ? _samples[slot * 2] : (short)0;
                destination[index + 1] = found ? _samples[slot * 2 + 1] : (short)0;
            }
        }
    }

    internal void Mix(long firstFrame, Span<short> destination)
    {
        lock (_gate)
        {
            for (var index = 0; index < destination.Length; index += 2)
            {
                var frame = firstFrame + index / 2;
                var slot = (int)(frame % Capacity);
                if (_timestamps[slot] != frame + 1) continue;
                destination[index] = Combine(destination[index], _samples[slot * 2]);
                destination[index + 1] = Combine(destination[index + 1], _samples[slot * 2 + 1]);
            }
        }
    }

    // Keep a source at its recorded level when the other is silent and saturate
    // occasional peaks safely when the two streams overlap.
    private static short Combine(short first, short second) => (short)Math.Clamp(first + second, short.MinValue, short.MaxValue);
}
