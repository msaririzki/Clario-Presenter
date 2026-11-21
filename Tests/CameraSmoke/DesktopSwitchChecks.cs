using Clario_Presenter.Camera;
using Clario_Presenter.Capture;
using Clario_Presenter.Recording;
using Clario_Presenter.Services;
using Microsoft.Graphics.Canvas;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Windows.Storage;

namespace Clario_Presenter;

internal static class DesktopSwitchChecks
{
    public static async Task RunAsync(string reportPath, bool includeCamera = false)
    {
        try
        {
            var display = DisplayCatalogService.GetDisplays().First(display => display.IsPrimary);
            var origin = new Point(display.Bounds.Left + 100, display.Bounds.Top + 100);
            using var backdrop = new PatternWindow(origin, new Size(640, 360), backdrop: true);
            using var publicApp = new PatternWindow(new Point(origin.X + 60, origin.Y + 60), new Size(200, 180));
            using var secondApp = new PatternWindow(new Point(origin.X + 300, origin.Y + 80), new Size(200, 180));
            backdrop.Show();
            publicApp.Show();
            secondApp.Show();
            var desktopBounds = new DisplayBounds(origin.X, origin.Y, 640, 360);
            if (WindowCatalogService.HasPrivateWindowOnDisplay(desktopBounds, [])
                || !WindowCatalogService.HasPrivateWindowOnDisplay(desktopBounds, [backdrop.Handle]))
                throw new InvalidOperationException("Private window detection must respect the selected window list.");
            using var capture = new CaptureSessionService();
            var held = false;
            capture.ConfigureDesktop(display.Bounds, display.WorkArea);
            capture.FrameHoldPredicate = () => held || WindowCatalogService.RequiresProtectedDesktop(
                display.Bounds, [], backdrop.Handle, presenterExcluded: true, privatePresenterThumbnail: true);
            capture.StartMonitor(display.Handle, keepLastFrame: true);
            var scene = new TestRegion(capture, desktopBounds, display.Bounds);
            var publicHandles = new[] { publicApp.Handle, secondApp.Handle };
            await Task.Delay(750);
            var temp = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
            var folder = await temp.CreateFolderAsync("DesktopSwitchSmoke",
                CreationCollisionOption.OpenIfExists);
            var file = await folder.CreateFileAsync("switches.mp4", CreationCollisionOption.GenerateUniqueName);
            await using var recorder = new OutputRecordingService();
            CameraOverlayOptions? cameraOptions = null;
            if (includeCamera)
            {
                var devices = await CameraCaptureService.GetDevicesAsync();
                var device = devices.FirstOrDefault() ?? throw new InvalidOperationException("No connected camera.");
                cameraOptions = new CameraOverlayOptions { Enabled = true, DeviceId = device.Id };
            }
            await recorder.StartAsync(scene, CaptureScaleMode.Fit, 1, () => ClientOutputMode.Live,
                await file.OpenAsync(FileAccessMode.ReadWrite), new RecordingOptions(60, 10, cameraOptions));
            using var inspection = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 640, 360, 96);
            // Exercise a compositor-owned overlay that is not in the public-window list.
            // A second top-level window with the same class is common with multiple monitors.
            using (var popup = new ThumbnailPopup(origin.X + 20, origin.Y + 20))
            {
                await Task.Delay(300);
                if (!WindowCatalogService.IsApplicationSwitcherActive()
                    || WindowCatalogService.RequiresProtectedDesktop(display.Bounds, [], backdrop.Handle, true, true)
                    || !WindowCatalogService.RequiresProtectedDesktop(display.Bounds, [], backdrop.Handle, true, false)
                    || !WindowCatalogService.RequiresProtectedDesktop(display.Bounds, [secondApp.Handle], backdrop.Handle, true, true))
                    throw new InvalidOperationException("Unexpected thumbnail protection for monitor capture.");
                using (var drawing = inspection.CreateDrawingSession()) scene.DrawRecordingFrame(drawing, 640, 360);
                var sample = inspection.GetPixelBytes(36, 36, 1, 1);
                if (sample[0] < 245 || sample[1] < 245 || sample[2] < 245)
                    throw new InvalidOperationException("Public shell popup missing from the desktop frame.");
            }
            await Task.Delay(180);
            var checks = 0;
            var publicFrameSignatures = new HashSet<int>();
            for (var pass = 0; pass < 10; pass++)
            {
                held = true;
                capture.ProtectDesktop();
                backdrop.Activate();
                backdrop.PrivatePattern = true;
                backdrop.Invalidate();
                capture.SetProtectedWindows(publicHandles, retainSceneIfEmpty: true);
                publicApp.Width = pass % 2 == 0 ? 220 : 200;
                await Task.Delay(180);
                using (var drawing = inspection.CreateDrawingSession())
                    scene.DrawRecordingFrame(drawing, 640, 360);
                var pixels = inspection.GetPixelBytes();
                var offset = (20 * 640 + 20) * 4;
                if (pixels[offset] > 180 && pixels[offset + 2] > 180 && pixels[offset + 1] < 80)
                    throw new InvalidOperationException("A private magenta frame leaked into the clean output.");
                if (pixels[offset] < 20 && pixels[offset + 1] < 20 && pixels[offset + 2] < 20)
                    throw new InvalidOperationException("The safe desktop became blank during a switch.");
                checks++;
                var signature = 0;
                for (var y = 80; y < 220; y += 8)
                    for (var x = 80; x < 220; x += 8)
                        signature = unchecked(signature * 31 + pixels[(y * 640 + x) * 4 + 1]);
                publicFrameSignatures.Add(signature);
                backdrop.PrivatePattern = false;
                backdrop.Invalidate();
                await Task.Delay(100);
                held = false;
                if (!capture.IsDesktopProtected)
                    throw new InvalidOperationException("Desktop protection was released before a fresh frame arrived.");
                publicApp.Activate();
                capture.ResumeDesktop();
                await Task.Delay(350);
                if (capture.PreparedWindowCount != 0 || capture.IsDesktopProtected)
                    throw new InvalidOperationException("Monitor capture did not resume without window captures.");
                using (var drawing = inspection.CreateDrawingSession())
                    scene.DrawRecordingFrame(drawing, 640, 360);
                var monitorPixels = inspection.GetPixelBytes();
                for (var y = 0; y < 360; y++)
                {
                    var yellowRun = 0;
                    for (var x = 0; x < 640; x++)
                    {
                        var i = (y * 640 + x) * 4;
                        yellowRun = monitorPixels[i] < 100 && monitorPixels[i + 1] > 145 && monitorPixels[i + 2] > 145
                            ? yellowRun + 1 : 0;
                        if (yellowRun >= 40) throw new InvalidOperationException("A yellow capture border reached the desktop frame.");
                    }
                }
                if (recorder.CameraWarning is { } warning) throw new InvalidOperationException(warning);
            }
            await recorder.StopAsync();
            if (recorder.Failure is not null) throw recorder.Failure;
            if (capture.PublicCaptureStartCount != 2 * checks)
                throw new InvalidOperationException("Expected two window captures per private interval.");
            if (publicFrameSignatures.Count < 3)
                throw new InvalidOperationException("The public app did not keep moving while private content was active.");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Passed = true, PublicShellPopup = true, PrivateSwitches = checks, PublicCaptureStarts = capture.PublicCaptureStartCount,
                DistinctPublicFrames = publicFrameSignatures.Count, Recording = file.Path,
                Bytes = (await file.GetBasicPropertiesAsync()).Size, WithCamera = recorder.IncludesCamera
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = false, Error = exception.ToString() },
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    // Encode only the synthetic windows within the monitor capture.
    internal sealed class TestRegion(CaptureSessionService capture, DisplayBounds region, DisplayBounds monitor) : IRecordingFrameSource
    {
        public bool HasFrame => capture.HasFrame;
        public void DrawRecordingFrame(CanvasDrawingSession session, float width, float height,
            CaptureScaleMode scaleMode = CaptureScaleMode.Fit, float adaptiveZoom = 1)
        {
            var transform = session.Transform;
            try
            {
                session.Transform = System.Numerics.Matrix3x2.CreateTranslation(monitor.Left - region.Left, monitor.Top - region.Top)
                    * System.Numerics.Matrix3x2.CreateScale(width / region.Width, height / region.Height) * transform;
                capture.DrawRecordingFrame(session, monitor.Width, monitor.Height);
            }
            finally { session.Transform = transform; }
        }
    }

    private sealed class ThumbnailPopup : IDisposable
    {
        private const string ClassName = "TaskListThumbnailWnd";
        private readonly nint _module = GetModuleHandle(null);
        private readonly nint _window;

        public ThumbnailPopup(int x, int y)
        {
            var cls = new PopupClass { Name = ClassName, Instance = _module, Background = GetStockObject(0),
                Procedure = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW") };
            if (RegisterClass(ref cls) == 0) throw new System.ComponentModel.Win32Exception();
            _window = CreateWindowEx(0x08000088, ClassName, "", 0x90000000, x, y, 48, 48, 0, 0, _module, 0);
            if (_window == 0)
            {
                var error = new System.ComponentModel.Win32Exception();
                UnregisterClass(ClassName, _module);
                throw error;
            }
        }

        public void Dispose()
        {
            DestroyWindow(_window);
            UnregisterClass(ClassName, _module);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PopupClass
        {
            public uint Style;
            public nint Procedure;
            public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background, MenuName;
            public string Name;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClass(ref PopupClass cls);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool UnregisterClass(string name, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowEx(uint exStyle, string cls, string title, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
        [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern nint GetProcAddress(nint module, string name);
    }

    private sealed class PatternWindow : Form
    {
        private readonly bool _backdrop;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
        private int _frame;
        public bool PrivatePattern;
        protected override bool ShowWithoutActivation => true;

        public PatternWindow(Point location, Size size, bool backdrop = false)
        {
            _backdrop = backdrop;
            Text = "Clario public/private capture test";
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            Location = location;
            Size = size;
            DoubleBuffered = true;
            _timer.Tick += (_, _) => { _frame++; Invalidate(); };
            _timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(_backdrop ? (PrivatePattern ? Color.Magenta : Color.FromArgb(24, 44, 70)) : Color.Green);
            if (!_backdrop)
            {
                using var brush = new SolidBrush(Color.FromArgb(40, 100 + _frame % 140, 65));
                e.Graphics.FillRectangle(brush, 0, 0, Width, Height);
                e.Graphics.FillRectangle(Brushes.White, _frame * 7 % Math.Max(1, Width), 30, 20, 90);
            }
            else e.Graphics.FillRectangle(Brushes.Gray, 0, Height - 40, Width, 40);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
