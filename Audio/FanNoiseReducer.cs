using RNNoise.NET;
using System.Runtime.InteropServices;

namespace Clario_Presenter.Audio;

// Keep partial WASAPI packets between callbacks. Process exactly 10 ms per
// channel and mix in a little time-aligned input to preserve soft consonants.
internal sealed class FanNoiseReducer : IDisposable
{
    internal static bool Supported => RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;
    private const int Size = 480;
    private readonly Channel _left = new();
    private Channel? _right;
    private int _offset;
    private bool _used;

    internal void Process(Span<short> pcm, int channels)
    {
        _used = true;
        if (channels == 2) _right ??= new Channel();
        for (int i = 0; i < pcm.Length; i += channels)
        {
            pcm[i] = _left.Exchange(_offset, pcm[i]);
            if (channels == 2) pcm[i + 1] = _right!.Exchange(_offset, pcm[i + 1]);
            if (++_offset != Size) continue;
            _offset = 0;
            _left.Filter();
            _right?.Filter();
        }
    }

    internal void Reset()
    {
        if (!_used) return;
        _used = false;
        _offset = 0;
        _left.Reset();
        _right?.Reset();
    }

    public void Dispose()
    {
        _left.Dispose();
        _right?.Dispose();
    }

    private sealed class Channel : IDisposable
    {
        private Denoiser? _model;
        private readonly float[] _frame = new float[Size];
        private readonly short[] _input = new short[Size], _previous = new short[Size];

        internal short Exchange(int index, short sample)
        {
            _input[index] = sample;
            var value = Math.Round(_frame[index] * short.MaxValue);
            return (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        }

        internal void Filter()
        {
            _model ??= new Denoiser();
            double energy = 0;
            foreach (var sample in _previous) energy += sample * sample;
            // Preserve faint consonants even when the speech model is uncertain.
            // For a loud stationary fan, retain only a small amount of the input.
            var rms = Math.Sqrt(energy / Size);
            var dry = (float)Math.Clamp(0.75 - (rms - 220) / 1300, 0.005, 0.75);
            for (int i = 0; i < Size; i++) _frame[i] = _input[i] / (float)short.MaxValue;
            _model.Denoise(_frame);
            for (int i = 0; i < Size; i++)
            {
                // RNNoise synthesizes the preceding frame (one overlap window).
                _frame[i] = (1 - dry) * _frame[i] + dry * _previous[i] / short.MaxValue;
                _previous[i] = _input[i];
            }
        }

        internal void Reset()
        {
            Dispose();
            Array.Clear(_frame);
            Array.Clear(_previous);
        }

        public void Dispose()
        {
            _model?.Dispose();
            _model = null;
        }
    }
}
