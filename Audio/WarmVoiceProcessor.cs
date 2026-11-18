namespace Clario_Presenter.Audio;

// Capture supplies 48 kHz, interleaved mono or stereo PCM16.
internal sealed class WarmVoiceProcessor
{
    private const int Rate = RecordingAudioFormat.SampleRate;
    private const double Threshold = 8200; // about -12 dBFS
    private const double Ceiling = 28700;  // leave room for AAC peaks
    private static readonly double HighPassPole = Math.Exp(-2 * Math.PI * 44 / Rate);
    private static readonly double Attack = Math.Exp(-1.0 / (Rate * 0.006));
    private static readonly double Release = Math.Exp(-1.0 / (Rate * 0.14));
    private static readonly PeakEq Body = new(190, 0.85, 3.8);
    private static readonly PeakEq Edge = new(3100, 1.05, -2.3);
    private readonly Channel[] _channels = new Channel[2];
    private double _envelope;
    private double _limiterGain = 1;

    internal void Reset()
    {
        Array.Clear(_channels);
        _envelope = 0;
        _limiterGain = 1;
    }

    internal void Process(Span<short> samples, int channels)
    {
        for (var i = 0; i < samples.Length; i += channels)
        {
            var left = _channels[0].Filter(samples[i]);
            var right = channels == 2 ? _channels[1].Filter(samples[i + 1]) : left;
            var peak = Math.Max(Math.Abs(left), Math.Abs(right));
            var decay = peak > _envelope ? Attack : Release;
            _envelope = peak + decay * (_envelope - peak);
            var gain = _envelope > Threshold ? Math.Sqrt(Threshold / _envelope) : 1;
            var postPeak = peak * gain;
            var limit = postPeak > Ceiling ? Ceiling / postPeak : 1;
            _limiterGain = Math.Min(limit, 1 + Release * (_limiterGain - 1));
            gain *= _limiterGain;
            samples[i] = (short)Math.Round(left * gain);
            if (channels == 2) samples[i + 1] = (short)Math.Round(right * gain);
        }
    }

    private struct Channel
    {
        private double _low;
        private FilterState _body, _edge;

        internal double Filter(double sample)
        {
            _low += (1 - HighPassPole) * (sample - _low);
            return Edge.Apply(Body.Apply(sample - _low, ref _body), ref _edge);
        }
    }

    private struct FilterState { internal double Z1, Z2; }

    // Constant-Q peaking EQ in transposed direct form II (RBJ Audio EQ Cookbook).
    private readonly struct PeakEq
    {
        private readonly double _b0, _b1, _b2, _a2;
        internal PeakEq(double frequency, double q, double gainDb)
        {
            var a = Math.Pow(10, gainDb / 40);
            var omega = 2 * Math.PI * frequency / Rate;
            var alpha = Math.Sin(omega) / (2 * q);
            var a0 = 1 + alpha / a;
            _b0 = (1 + alpha * a) / a0;
            _b1 = -2 * Math.Cos(omega) / a0; // a1 and b1 are equal
            _b2 = (1 - alpha * a) / a0;
            _a2 = (1 - alpha / a) / a0;
        }

        internal double Apply(double input, ref FilterState state)
        {
            var output = _b0 * input + state.Z1;
            state.Z1 = _b1 * (input - output) + state.Z2;
            state.Z2 = _b2 * input - _a2 * output;
            return output;
        }
    }
}
