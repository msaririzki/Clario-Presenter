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
                    (_, _, _, _, _) => throw new InvalidOperationException("Open-only check received a microphone packet."), stop.Token);
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
                    await Task.Delay(150);
                    if (loopback.Warning is not null) throw new InvalidOperationException(loopback.Warning);
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

            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                Passed = true, Inputs = devices.Inputs.Count, Outputs = devices.Outputs.Count,
                NativeLoopbackOpened = devices.Outputs.Count > 0, MissingInputRejected = true,
                Mixed = mixedFile.Path, VideoOnly = silentFile.Path
            }));
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = false, Stage = stage, Error = error.ToString() }));
        }
    }

    private static void VerifyMixer()
    {
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
    }

    private sealed class SyntheticAudio : IRecordingAudioSource
    {
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public void StartClock(long timestamp) { }
        public string? Warning => null;
        public Task Completion => Task.CompletedTask;

        public void Read(long firstFrame, Span<short> samples)
        {
            for (var index = 0; index < samples.Length; index += 2)
            {
                var time = (firstFrame + index / 2) / (double)RecordingAudioFormat.SampleRate;
                var input = (short)(Math.Sin(time * Math.Tau * 440) * 5800 + Math.Sin(time * Math.Tau * 660) * 3600);
                samples[index] = input;
                samples[index + 1] = input;
            }
        }
    }

    private sealed class SyntheticFrame : IRecordingFrameSource
    {
        public bool HasFrame => true;
        public void DrawRecordingFrame(CanvasDrawingSession session, float width, float height,
            CaptureScaleMode scaleMode, float adaptiveZoom) => session.Clear(Color.FromArgb(255, 29, 45, 70));
    }
}
