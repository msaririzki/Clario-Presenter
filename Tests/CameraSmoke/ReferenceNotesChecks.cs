using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Clario_Presenter.Capture;
using Clario_Presenter.Notes;
using Clario_Presenter.Recording;
using Clario_Presenter.Services;
using Microsoft.Graphics.Canvas;
using Windows.Storage;

namespace Clario_Presenter;

internal static class ReferenceNotesChecks
{
    internal static void RunFixture(string title, string mode)
    {
        var display = Screen.PrimaryScreen!.WorkingArea;
        using var form = new Form
        {
            Text = title, StartPosition = FormStartPosition.Manual, TopMost = true,
            FormBorderStyle = FormBorderStyle.None,
            Bounds = new Rectangle(display.Right - (mode == "private" ? 440 : 530), display.Top + 100, 350, 220),
            BackColor = mode == "private" ? Color.Magenta : Color.FromArgb(20, 170, 45)
        };
        using var sibling = new Form
        {
            Text = title + " sibling", StartPosition = FormStartPosition.Manual, TopMost = true,
            FormBorderStyle = FormBorderStyle.None,
            Bounds = new Rectangle(form.Left - 90, form.Top + 20, 110, 100),
            BackColor = Color.Magenta
        };
        if (mode == "private") form.Shown += (_, _) => sibling.Show();
        using var timer = new System.Windows.Forms.Timer { Interval = 120 };
        timer.Tick += (_, _) => form.BackColor = form.BackColor.G == 170
            ? Color.FromArgb(20, 220, 45) : Color.FromArgb(20, 170, 45);
        if (mode == "public") timer.Start();
        Application.Run(form);
    }

    internal static async Task RunAsync(string reportPath)
    {
        // Nested async waits must keep running on the message thread that owns the capture and native panel.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var stage = "fixtures";
        var key = Guid.NewGuid().ToString("N");
        using var publicApp = StartFixture(key + "-public", "public");
        using var privateApp = StartFixture(key + "-private", "private");
        try
        {
            var publicWindow = await FindWindowAsync(key + "-public");
            var reference = await FindWindowAsync(key + "-private");
            var sibling = await FindWindowAsync(key + "-private sibling");
            var display = DisplayCatalogService.GetDisplays().First(item => item.IsPrimary);
            var region = new DisplayBounds(display.WorkArea.Left + display.WorkArea.Width - 530,
                display.WorkArea.Top + 100, 440, 220);
            var publicWindows = WindowCatalogService.GetPublicDesktopWindows(display.WorkArea, [], reference);
            if (!WindowCatalogService.IsReferenceWindow(sibling.Handle, reference)
                || publicWindows.Contains(reference.Handle) || publicWindows.Contains(sibling.Handle)
                || !publicWindows.Contains(publicWindow.Handle))
                throw new InvalidOperationException("Window filtering did not exclude the source and its independent sibling.");

            stage = "live overlay";
            using var notes = new FloatingNoteWindow(registerShortcuts: false);
            notes.SetReference(reference);
            notes.ApplyMonitors(display, display, null);
            notes.SetClickThrough(true);
            notes.Show(display);
            await WaitAsync(() => notes.HasReferenceFrame);
            if (!notes.IsShowing || !notes.ExcludedFromCapture)
                throw new InvalidOperationException(notes.Status + " " + notes.AffinityDiagnostic);
            var point = new Point(region.Left + 230, region.Top + 40);
            if (WindowFromPoint(point) == notes.Handle)
                throw new InvalidOperationException("The reference received a click through its body.");
            var foreground = GetForegroundWindow();
            SendMessage(notes.Handle, 0x312, 4, 0);
            if (notes.ReferenceZoom != 1.25f || GetForegroundWindow() != foreground)
                throw new InvalidOperationException("The reference zoom did not preserve the foreground app.");

            stage = "record public windows";
            using var capture = new CaptureSessionService();
            capture.ConfigureDesktop(display.Bounds, display.WorkArea);
            var publicHandles = publicWindows.Where(handle => handle == publicWindow.Handle).ToArray();
            SetWindowPos(publicWindow.Handle, -1, 0, 0, 0, 0, 0x13);
            capture.SetProtectedWindows(publicHandles);
            await WaitAsync(() => capture.SetProtectedWindows(publicHandles));
            SetWindowPos(reference.Handle, -1, 0, 0, 0, 0, 0x13);
            var scene = new DesktopSwitchChecks.TestRegion(capture, region, display.Bounds);
            using var output = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 440, 220, 96);
            var colors = new HashSet<byte>();
            var samples = new HashSet<string>();
            WindowCatalogService.TryGetCaptureBounds(publicWindow.Handle, out var publicBounds);
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(reportPath)!);
            var file = await folder.CreateFileAsync("reference-test.mp4", CreationCollisionOption.GenerateUniqueName);
            await using var recorder = new OutputRecordingService();
            await recorder.StartAsync(scene, CaptureScaleMode.Fit, 1, () => ClientOutputMode.Live,
                await file.OpenAsync(FileAccessMode.ReadWrite), new RecordingOptions(30, 10, null));
            for (var frame = 0; frame < 20; frame++)
            {
                await Task.Delay(110);
                using (var drawing = output.CreateDrawingSession()) scene.DrawRecordingFrame(drawing, 440, 220);
                var sample = output.GetPixelBytes(230, 40, 1, 1);
                samples.Add(Convert.ToHexString(sample));
                if (sample[0] < 65 && sample[2] < 60 && sample[1] > 130) colors.Add(sample[1]);
                var pixels = output.GetPixelBytes();
                for (var i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] > 185 && pixels[i + 2] > 185 && pixels[i + 1] < 60)
                        throw new InvalidOperationException("Private magenta pixels reached public output.");
                if (frame == 6) ShowWindow(reference.Handle, 6);
                if (frame == 10) ShowWindow(reference.Handle, 9);
                if (frame == 14) PostMessage(reference.Handle, 0x10, 0, 0);
            }
            await recorder.StopAsync();
            if (recorder.Failure is { } failure) throw failure;
            if (colors.Count < 2 || notes.HasReferenceFrame)
                throw new InvalidOperationException($"Public colors: {colors.Count}; layers: {capture.PreparedWindowCount}; ready: {capture.SetProtectedWindows(publicHandles)}; private source live: {notes.HasReferenceFrame}. Bounds: {publicBounds}; region: {region}. Samples: {string.Join(", ", samples)}.");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = true, DistinctPublicColors = colors.Count, Recording = file.Path }));
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = false, Stage = stage, Error = exception.ToString() }));
        }
        finally
        {
            if (!privateApp.HasExited) privateApp.Kill();
            if (!publicApp.HasExited) publicApp.Kill();
        }
    }

    private static Process StartFixture(string title, string mode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("--note-fixture");
        start.ArgumentList.Add(title);
        start.ArgumentList.Add(mode);
        return Process.Start(start)!;
    }

    private static async Task<WindowSource> FindWindowAsync(string title)
    {
        WindowSource? result = null;
        await WaitAsync(() => (result = WindowCatalogService.GetPresentableWindows().FirstOrDefault(item => item.Title == title)) is not null);
        return result!;
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var timeout = Stopwatch.StartNew();
        while (!ready())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("The expected window or frame did not appear.");
            await Task.Delay(100);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
