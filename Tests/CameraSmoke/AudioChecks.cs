using Clario_Presenter.Audio;
using Clario_Presenter.Capture;
using Clario_Presenter.Recording;
using Microsoft.Graphics.Canvas;
using System.Text.Json;
using Windows.Storage;
using Windows.UI;

namespace Clario_Presenter;

internal static class AudioChecks
{
    internal static async Task OpenInputOnlyAsync(string report)
    {
        try
        {
            await Task.Run(() =>
            {
                var device = Wasapi.Enumerate().Inputs.First();
                using var stop = new CancellationTokenSource();
                Wasapi.Capture(device.Id, false, stop.Cancel,
                    (_, _, _, _, _, _) => throw new InvalidOperationException("Open-only check received a microphone packet."), stop.Token);
                if (!stop.IsCancellationRequested) throw new InvalidOperationException("Microphone was not opened.");
            });
            File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, InputOpenedWithoutReading = true }));
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = false, Error = error.ToString() }));
        }
    }

    internal static async Task RunAsync(string report)
    {
        var stage = "timestamped mixer";
        void Progress() => File.WriteAllText(report, JsonSerializer.Serialize(new { Running = true, Stage = stage }));
        try
        {
            Progress();
            VerifyMixer();
            stage = "Windows device enumeration";
            Progress();
            var devices = await Task.Run(Wasapi.Enumerate);

            // Open the native playback endpoint while discarding all packets.
            // Only synthetic PCM is written to the test video.
            stage = "WASAPI playback initialization";
            Progress();
            if (devices.Outputs.Count > 0)
            {
                using var stop = new CancellationTokenSource();
                var loopback = new RecordingAudioSource(new(true, devices.Outputs[0].Id), () => true);
                try
                {
                    await loopback.StartAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(5));
                    loopback.MeteringEnabled = true;
                    await Task.Delay(150);
                    if (loopback.Warning is not null) throw new InvalidOperationException(loopback.Warning);
                    if (loopback.ReadLevel(true) is not { IsReady: true, Peak: 0 } || loopback.ReadLevel(false) is not null)
                        throw new InvalidOperationException("A muted output and a disabled microphone must remain distinguishable.");
                }
                finally
                {
                    stop.Cancel();
                    await loopback.Completion;
                }
            }

            stage = "missing microphone rejection";
            Progress();
            using (var stop = new CancellationTokenSource())
            {
                var missing = new RecordingAudioSource(new(MicrophoneEnabled: true, MicrophoneDeviceId: "clario-invalid-endpoint"),
                    () => false);
                try
                {
                    await missing.StartAsync(stop.Token);
                    throw new InvalidOperationException("Missing input silently fell back to a microphone.");
                }
                catch (InvalidOperationException error) when (error.Message.StartsWith("Mikrofon tidak bisa dibuka")) { }
                finally
                {
                    stop.Cancel();
                    await missing.Completion;
                }
            }

            var folder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync("AudioSmoke",
                CreationCollisionOption.OpenIfExists);
            var mixedFile = await folder.CreateFileAsync("audio-mix.mp4", CreationCollisionOption.ReplaceExisting);
            var silentFile = await folder.CreateFileAsync("video-only.mp4", CreationCollisionOption.ReplaceExisting);
            var warmFile = await folder.CreateFileAsync("audio-warm.mp4", CreationCollisionOption.ReplaceExisting);
            var onsetFile = await folder.CreateFileAsync("audio-onset.mp4", CreationCollisionOption.ReplaceExisting);
            stage = "AAC recording with privacy";
            Progress();
            var mode = (int)ClientOutputMode.Live;
            await using (var recorder = new OutputRecordingService(new SyntheticAudio()))
            {
                await recorder.StartAsync(new SyntheticFrame(), CaptureScaleMode.Fit, 1,
                    () => (ClientOutputMode)Volatile.Read(ref mode), await mixedFile.OpenAsync(FileAccessMode.ReadWrite));
                await Task.Delay(1050);
                Volatile.Write(ref mode, (int)ClientOutputMode.Privacy);
                recorder.SetAudioPrivacy(true);
                await Task.Delay(950);
                Volatile.Write(ref mode, (int)ClientOutputMode.Live);
                recorder.SetAudioPrivacy(false);
                await Task.Delay(900);
                await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
                if (recorder.Failure is not null) throw recorder.Failure;
            }

            stage = "video-only remains available";
            Progress();
            await using (var recorder = new OutputRecordingService())
            {
                await recorder.StartAsync(new SyntheticFrame(), CaptureScaleMode.Fit, 1,
                    () => ClientOutputMode.Live, await silentFile.OpenAsync(FileAccessMode.ReadWrite));
                await Task.Delay(800);
                await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
                if (recorder.Failure is not null) throw recorder.Failure;
            }

            stage = "quiet microphone onset before first screen frame";
            Progress();
            var onset = new OpeningAudio();
            await using (var recorder = new OutputRecordingService(onset))
            {
                var screen = new SyntheticFrame { HasFrame = false };
                await recorder.StartAsync(screen, CaptureScaleMode.Fit, 1, () => ClientOutputMode.Live,
                    await onsetFile.OpenAsync(FileAccessMode.ReadWrite));
                await Task.Delay(350);
                screen.HasFrame = true;
                await Task.Delay(850);
                await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
                if (recorder.Failure is not null) throw recorder.Failure;
            }

            stage = "warm narration AAC";
            Progress();
            await using (var recorder = new OutputRecordingService(new SyntheticAudio(warm: true)))
            {
                await recorder.StartAsync(new SyntheticFrame(), CaptureScaleMode.Fit, 1, () => ClientOutputMode.Live,
                    await warmFile.OpenAsync(FileAccessMode.ReadWrite));
                await Task.Delay(1000);
                await recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
                if (recorder.Failure is not null) throw recorder.Failure;
            }

            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                Passed = true, Inputs = devices.Inputs.Count, Outputs = devices.Outputs.Count,
                NativeLoopbackOpened = devices.Outputs.Count > 0, MissingInputRejected = true,
                Mixed = mixedFile.Path, VideoOnly = silentFile.Path, Warm = warmFile.Path, Onset = onsetFile.Path
            }));
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = false, Stage = stage, Error = error.ToString() }));
        }
    }

    private static void VerifyMixer()
    {
        var voice = new WarmVoiceProcessor();
        var stereo = new short[960];
        for (int i = 0; i < stereo.Length; i += 2) stereo[i] = short.MaxValue;
        voice.Process(stereo, 2);
        for (int i = 0; i < stereo.Length; i += 2)
            if (Math.Abs((int)stereo[i]) > 28700 || stereo[i + 1] != 0)
                throw new InvalidOperationException("Warm narration exceeded its peak ceiling or leaked across channels.");
        voice.Reset();
        Array.Clear(stereo);
        voice.Process(stereo, 1);
        if (stereo.Any(sample => sample != 0)) throw new InvalidOperationException("Filter history survived a reset.");
        var accumulated = 0;
        using (var reducer = new FanNoiseReducer())
        {
            // Exercise the native dependency, partial packets, stereo and a privacy boundary.
            var random = new Random(43);
            var fan = new short[48000 * 2];
            for (int i = 0; i < fan.Length; i++) fan[i] = (short)random.Next(-5500, 5501);
            double Energy(short[] pcm) => pcm.Skip(pcm.Length / 2).Average(x => (double)x * x);
            var before = Energy(fan);
            for (int n = 0; n < fan.Length; n += 640)
                reducer.Process(fan.AsSpan(n, Math.Min(640, fan.Length - n)), 2);
            if (Energy(fan) > before / 2) throw new InvalidOperationException("Broadband noise was not attenuated by at least 3 dB.");
            reducer.Reset();
            Array.Clear(fan);
            reducer.Process(fan.AsSpan(0, 4800), 1);
            if (fan.Any(x => x != 0)) throw new InvalidOperationException("Private audio survived a reducer reset.");
            // After a pause, preserve a soft 20 ms onset even if the model
            // classifies it as background. The fixed pipeline delay is 20 ms.
            var onset = new short[2400];
            for (int i = 0; i < 960; i++) onset[i] = (short)(180 * Math.Sin(i * Math.Tau * 550 / 48000));
            reducer.Process(onset, 1);
            if (AudioLevelMonitor.PeakOf(onset.AsSpan(960, 480)) < 90)
                throw new InvalidOperationException("Quiet microphone onset disappeared.");
        }
        var roomTone = Enumerable.Repeat((short)35, 480).ToArray();
        roomTone[123] = 350;
        AudioLevelMonitor.Accumulate(ref accumulated, roomTone, microphone: true);
        if (accumulated != 0) throw new InvalidOperationException("Quiet microphone noise moved the RMS meter.");
        roomTone[123] = short.MinValue;
        AudioLevelMonitor.Accumulate(ref accumulated, roomTone, microphone: true);
        if (accumulated != 32768) throw new InvalidOperationException("Lost a clipping warning.");
        accumulated = 0;
        AudioLevelMonitor.Accumulate(ref accumulated, [0, -1024]);
        AudioLevelMonitor.Accumulate(ref accumulated, [512, 127]);
        if (accumulated != 1024) throw new InvalidOperationException("Lost the highest audio level between UI updates.");
        if (AudioLevelMonitor.PeakOf([100, -200, short.MinValue]) != 32768 || AudioLevelMonitor.PeakOf([0, 0]) != 0)
            throw new InvalidOperationException("The level meter must handle silence and full-scale negative PCM.");
        var integrated = new AudioDevice("internal", "Internal", Origin: AudioInputOrigin.Integrated);
        var virtualInput = new AudioDevice("virtual", "Virtual", Origin: AudioInputOrigin.Software);
        var externalDefault = new AudioDevice("usb", "USB");
        if (Wasapi.SelectAutomaticInput([integrated, virtualInput], virtualInput) != integrated
            || Wasapi.SelectAutomaticInput([integrated, externalDefault], externalDefault) != externalDefault
            || Wasapi.SelectAutomaticInput([integrated, integrated with { Id = "other" }, virtualInput], virtualInput) != virtualInput)
            throw new InvalidOperationException("Automatic input selection must prefer a known built-in input only when the Windows default is software.");
        var playback = new PcmTimeline();
        playback.Store(-1, [99, 99, 10_000, 20_000], channels: 2);
        playback.Store(100, [5, 7], channels: 2);
        var microphone = new PcmTimeline();
        microphone.Store(0, [15_000], channels: 1);
        microphone.Store(3, [700], channels: 1);
        Span<short> output = stackalloc short[8];
        playback.Read(0, output);
        microphone.Mix(0, output);
        if (!output.SequenceEqual(new short[] { 25_000, short.MaxValue, 0, 0, 0, 0, 700, 700 }))
            throw new InvalidOperationException("Mixer changed a gap, mono input, or saturated sample.");
        playback.Read(99, output);
        if (!output.SequenceEqual(new short[] { 0, 0, 5, 7, 0, 0, 0, 0 }))
            throw new InvalidOperationException("A late packet moved earlier into a silent gap.");

        var clock = new CapturePacketClock();
        const int packet = 480;
        for (int i = 0; i < 50; i++)
        {
            int expected = i * packet;
            long stamp = expected + (i == 0 ? 0 : (i % 2 == 0 ? -19 : 27));
            var flags = i == 4 ? Wasapi.TimestampError : i == 5 ? Wasapi.Silent : 0u;
            if (clock.Place(packet, (ulong)expected, flags, stamp, expected + 950) != expected)
                throw new InvalidOperationException("Timestamp jitter split a contiguous PCM packet.");
        }
        const int afterDrop = 51 * packet;
        if (clock.Place(packet, afterDrop, Wasapi.Discontinuity, afterDrop, afterDrop + 600) != afterDrop)
            throw new InvalidOperationException("A real capture discontinuity must keep its position on the video clock.");
    }

    private sealed class SyntheticAudio(bool warm = false) : IRecordingAudioSource
    {
        private readonly WarmVoiceProcessor? _voice = warm ? new() : null;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public void StartClock(long timestamp) { }
        public string? Warning => null;
        public Task Completion => Task.CompletedTask;

        public void Read(long firstFrame, Span<short> samples)
        {
            for (var index = 0; index < samples.Length; index += 2)
            {
                var time = (firstFrame + index / 2) / (double)RecordingAudioFormat.SampleRate;
                var input = (short)(Math.Sin(time * Math.Tau * 440) * 5800 + Math.Sin(time * Math.Tau * 660) * 3600
                    + Math.Sin(time * Math.Tau * 120) * 2500 + Math.Sin(time * Math.Tau * 4500) * 1800);
                samples[index] = input;
                samples[index + 1] = input;
            }
            _voice?.Process(samples, 2);
        }
    }

    private sealed class OpeningAudio : IRecordingAudioSource
    {
        private readonly PcmTimeline _timeline = new();
        private bool _armed;
        public void StartClock(long timestamp) => _armed = true;
        public Task Completion => Task.CompletedTask;
        public string? Warning => null;

        public async Task StartAsync(CancellationToken token)
        {
            if (!_armed) throw new InvalidOperationException("First mic packet arrived before the recorder clock started.");
            await Task.Delay(120, token);
            var voice = new WarmVoiceProcessor();
            var pcm = new short[RecordingAudioFormat.SampleRate];
            for (int i = 0; i < pcm.Length; i++)
            {
                int offset = i % 33600; // repeat after a 700 ms interval
                var amplitude = offset < 4800 ? 220 : offset < 11040 ? 2800 : 0;
                pcm[i] = (short)(amplitude * Math.Sin(i * Math.Tau * 550 / RecordingAudioFormat.SampleRate));
            }
            voice.Process(pcm, 1);
            _timeline.Store(0, pcm, 1);
        }
        public void Read(long firstFrame, Span<short> samples) => _timeline.Read(firstFrame, samples);
    }

    private sealed class SyntheticFrame : IRecordingFrameSource
    {
        public bool HasFrame { get; set; } = true;
        public void DrawRecordingFrame(CanvasDrawingSession session, float width, float height,
            CaptureScaleMode scaleMode, float adaptiveZoom) => session.Clear(Color.FromArgb(255, 29, 45, 70));
    }
}
