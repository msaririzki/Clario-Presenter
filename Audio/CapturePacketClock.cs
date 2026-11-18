namespace Clario_Presenter.Audio;

// Anchor to the video clock, then place consecutive PCM packets sample-to-sample.
// Hardware timestamps vary slightly even when the device delivered every sample.
internal sealed class CapturePacketClock
{
    private long? _nextFrame;
    private ulong _nextDevicePosition;

    internal long Place(int frames, ulong position, uint flags, long? timestampFrame, long arrivalFrame)
    {
        const int rate = RecordingAudioFormat.SampleRate;
        var valid = (flags & Wasapi.TimestampError) == 0 && timestampFrame.HasValue
            && timestampFrame.Value >= arrivalFrame - rate && timestampFrame.Value <= arrivalFrame + rate / 10;
        var anchor = valid ? timestampFrame.GetValueOrDefault() : arrivalFrame - frames;
        var start = anchor;
        if (_nextFrame is long next && (flags & Wasapi.Discontinuity) == 0
            && (!valid || position == _nextDevicePosition) && Math.Abs(anchor - next) < rate / 10)
        {
            start = next;
            // Correct slow device-clock drift gradually, without punching packet-sized holes.
            if (valid && Math.Abs(anchor - next) > rate / 100) start += Math.Sign(anchor - next);
        }
        _nextFrame = start + frames;
        _nextDevicePosition = position + (uint)frames;
        return start;
    }
}
