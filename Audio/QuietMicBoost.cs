namespace Clario_Presenter.Audio;

// Fixed gain keeps pauses quiet instead of turning up room noise during silence.
// A stereo-linked limiter reduces the gain immediately for loud transients.
internal sealed class QuietMicBoost
{
    private const double Gain = 2.818382931; // +9 dB
    private const double Ceiling = 29204; // -1 dBFS
    private static readonly double Release = Math.Exp(-1.0 / (RecordingAudioFormat.SampleRate * 0.12));
    private double _gain = Gain;

    internal static QuietMicBoost? ForEndpoint(string endpointId, IReadOnlyList<string> enabled) =>
        enabled.Contains(endpointId, StringComparer.OrdinalIgnoreCase) ? new() : null;

    internal void Reset() => _gain = Gain;

    internal void Process(Span<short> pcm, int channels)
    {
        for (int i = 0; i < pcm.Length; i += channels)
        {
            int peak = Math.Abs((int)pcm[i]);
            if (channels == 2) peak = Math.Max(peak, Math.Abs((int)pcm[i + 1]));
            double target = peak * Gain > Ceiling ? Ceiling / peak : Gain;
            _gain = target <= _gain ? target : target + Release * (_gain - target);
            pcm[i] = (short)Math.Round(pcm[i] * _gain);
            if (channels == 2) pcm[i + 1] = (short)Math.Round(pcm[i + 1] * _gain);
        }
    }
}
