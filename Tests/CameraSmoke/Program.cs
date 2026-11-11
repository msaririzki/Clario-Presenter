using Clario_Presenter.Camera;
using Clario_Presenter.Capture;
using Clario_Presenter.Recording;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using System.Text.Json;
using Windows.Storage;
using Windows.UI;

namespace Clario_Presenter;

public enum ClientOutputMode { Live, Frozen, Privacy, SourceLost }

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var reportPath = args.FirstOrDefault(arg => !arg.StartsWith("--"))
            ?? Path.Combine(Path.GetTempPath(), "Clario-CameraSmoke.json");
        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcher = DispatcherQueueController.CreateOnDedicatedThread();
            dispatcher.DispatcherQueue.TryEnqueue(async () =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                if (args.Contains("--desktop-only")) await DesktopSwitchChecks.RunAsync(reportPath);
                else await RunChecksAsync(reportPath);
                completion.TrySetResult();
            });
            completion.Task.GetAwaiter().GetResult();
            dispatcher.ShutdownQueueAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = false, Error = exception.ToString() }));
        }
    }

    private static async Task RunChecksAsync(string reportPath)
    {
        var stage = "enumerate";
        try
        {
            var devices = await CameraCaptureService.GetDevicesAsync();
            var device = devices.FirstOrDefault() ?? throw new InvalidOperationException("No connected camera.");
            stage = "missing device validation";
            await using (var unavailableCamera = new CameraCaptureService())
            {
                try
                {
                    await unavailableCamera.StartAsync("clario-smoke-missing-device");
                    throw new InvalidOperationException("An unavailable device must not fall back to another camera.");
                }
                catch (InvalidOperationException exception) when (exception.Message.StartsWith("Kamera terpilih")) { }
            }
            stage = "preview";
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Running = true, Stage = stage, Device = device.Name }));
            await using (var camera = new CameraCaptureService())
            {
                await camera.StartAsync(device.Id);
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Running = true, Stage = "preview frames received" }));
                await Task.Delay(1000);
                if (camera.Warning is not null) throw new InvalidOperationException(camera.Warning);
                using var preview = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 640, 360, 96);
                using var session = preview.CreateDrawingSession();
                var originalTransform = session.Transform;
                if (!camera.Draw(session, new CameraRectangle(0, 0, 640, 360), mirror: true)
                    || session.Transform != originalTransform)
                    throw new InvalidOperationException("Mirrored camera drawing failed or changed the screen transform.");
            }

            stage = "recording";
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Running = true, Stage = stage }));
            var folder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync("CameraSmoke",
                CreationCollisionOption.OpenIfExists);
            var file = await folder.CreateFileAsync("camera-smoke.mp4", CreationCollisionOption.GenerateUniqueName);
            await using var recorder = new OutputRecordingService();
            var mode = ClientOutputMode.Live;
            await recorder.StartAsync(new SyntheticScreen(), CaptureScaleMode.Fit, 1, () => mode,
                await file.OpenAsync(FileAccessMode.ReadWrite),
                new RecordingOptions(60, 10, new CameraOverlayOptions { Enabled = true, DeviceId = device.Id }));
            await Task.Delay(2500);
            mode = ClientOutputMode.Privacy;
            await Task.Delay(750);
            mode = ClientOutputMode.Live;
            await Task.Delay(1500);
            await recorder.StopAsync();
            if (recorder.Failure is not null) throw recorder.Failure;
            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size < 1000) throw new InvalidOperationException("Recording has no encoded video.");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Passed = true, Devices = devices.Select(item => item.Name),
                Recording = file.Path, Bytes = properties.Size,
                CameraWarning = recorder.CameraWarning,
                MissingDeviceRejected = true, MirroredPreviewRendered = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Passed = false, Stage = stage, Error = exception.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private sealed class SyntheticScreen : IRecordingFrameSource
    {
        public bool HasFrame => true;

        public void DrawRecordingFrame(CanvasDrawingSession session, float width, float height,
            CaptureScaleMode scaleMode, float adaptiveZoom)
        {
            session.Clear(Color.FromArgb(255, 35, 65, 100));
            session.DrawText("ATAS - Clario Camera QA", 80, 80, Color.FromArgb(255, 255, 255, 255));
            session.DrawText("BAWAH - screen + camera 1080p60", 80, height - 120, Color.FromArgb(255, 255, 255, 255));
        }
    }
}
