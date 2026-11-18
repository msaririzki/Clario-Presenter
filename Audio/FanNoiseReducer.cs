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
    private int _tailFrames;

    internal void Process(Span<short> pcm, int channels)
    {
        _used = true;
        _tailFrames = 2 * Size;
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

    // A silent WASAPI packet still occupies time. Drain the overlap window so
    // the last consonant reaches the recorder before sleeping through silence.
    internal int Drain(Span<short> pcm, int channels)
    {
        var count = Math.Min(pcm.Length / channels, _tailFrames);
        if (count == 0) return 0;
        var remaining = _tailFrames - count;
        var tail = pcm[..(count * channels)];
        tail.Clear();
        Process(tail, channels);
        _tailFrames = remaining;
        if (remaining == 0) Reset();
        return count * channels;
    }

    internal void Reset()
    {
        if (!_used) return;
        _used = false;
        _offset = 0;
        _tailFrames = 0;
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
        private IntPtr _model;
        private int _quietFrames;
        private float _backgroundGain = 1;
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
            if (_model == IntPtr.Zero)
            {
                _model = Native.rnnoise_create(IntPtr.Zero);
                if (_model == IntPtr.Zero) throw new InvalidOperationException("Peredam suara kipas belum bisa dimulai.");
            }
            double energy = 0;
            foreach (var sample in _previous) energy += sample * sample;
            // Preserve faint consonants even when the speech model is uncertain.
            // For a loud stationary fan, retain only a small amount of the input.
            var rms = Math.Sqrt(energy / Size);
            var dry = (float)Math.Clamp(0.75 - (rms - 220) / 1300, 0.002, 0.75);
            for (int i = 0; i < Size; i++) _frame[i] = _input[i];
            var probability = ProcessFrame(_model, _frame, _frame);
            // Keep tails through short gaps. Reopen within one frame when
            // speech returns and keep a dry path for low-level consonants.
            _quietFrames = probability < 0.35f ? Math.Min(20, _quietFrames + 1) : 0;
            var target = _quietFrames >= 14 ? 0.012f : 1f;
            var initial = _backgroundGain;
            _backgroundGain += (target - initial) * (target < initial ? 0.18f : 1f);
            for (int i = 0; i < Size; i++)
            {
                // RNNoise synthesizes the preceding frame (one overlap window).
                var gain = initial + (_backgroundGain - initial) * (i + 1) / Size;
                _frame[i] = (float)(((1 - dry) * _frame[i] * gain + dry * _previous[i]) / short.MaxValue);
                _previous[i] = _input[i];
            }
        }

        internal void Reset()
        {
            Dispose();
            Array.Clear(_frame);
            Array.Clear(_previous);
            _quietFrames = 0;
            _backgroundGain = 1;
        }

        public void Dispose()
        {
            if (_model != IntPtr.Zero) Native.rnnoise_destroy(_model);
            _model = IntPtr.Zero;
        }

        [DllImport("rnnoise", EntryPoint = "rnnoise_process_frame", CallingConvention = CallingConvention.Cdecl)]
        private static extern float ProcessFrame(IntPtr model, [Out] float[] output, [In] float[] input);
    }
}
