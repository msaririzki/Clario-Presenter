using Clario_Presenter.Capture;
using Clario_Presenter.Recording;
using Clario_Presenter.Services;
using Microsoft.Graphics.Canvas;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Windows.Storage;

namespace Clario_Presenter;

internal static class DesktopSwitchChecks
{
    public static async Task RunAsync(string reportPath)
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
            using var capture = new CaptureSessionService();
            var held = false;
            capture.ConfigureDesktop(new(origin.X, origin.Y, 640, 360), new(origin.X, origin.Y, 640, 320));
            capture.FrameHoldPredicate = () => held;
            capture.Start(backdrop.Handle, keepLastFrame: true, captureCursor: false);
            var publicHandles = new[] { publicApp.Handle, secondApp.Handle };
            capture.SetProtectedWindows(publicHandles);
            await Task.Delay(750);
            var folder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync("DesktopSwitchSmoke",
                CreationCollisionOption.OpenIfExists);
            var file = await folder.CreateFileAsync("switches.mp4", CreationCollisionOption.GenerateUniqueName);
            await using var recorder = new OutputRecordingService();
            await recorder.StartAsync(capture, CaptureScaleMode.Fit, 1, () => ClientOutputMode.Live,
                await file.OpenAsync(FileAccessMode.ReadWrite), new RecordingOptions(60, 10));
            using var inspection = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 640, 360, 96);
            var checks = 0;
            var publicFrameSignatures = new HashSet<int>();
            for (var pass = 0; pass < 10; pass++)
            {
                held = true;
                capture.IsDesktopProtected = true;
                backdrop.PrivatePattern = true;
                backdrop.Invalidate();
                capture.SetProtectedWindows(publicHandles, retainSceneIfEmpty: true);
                publicApp.Width = pass % 2 == 0 ? 220 : 200;
                await Task.Delay(180);
                using (var drawing = inspection.CreateDrawingSession())
                    capture.DrawRecordingFrame(drawing, 640, 360);
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
                capture.IsDesktopProtected = false;
                capture.SetProtectedWindows(publicHandles);
                await Task.Delay(120);
            }
            await recorder.StopAsync();
            if (recorder.Failure is not null) throw recorder.Failure;
            if (capture.PublicCaptureStartCount != 2)
                throw new InvalidOperationException($"Public captures restarted: {capture.PublicCaptureStartCount} starts for two apps.");
            if (publicFrameSignatures.Count < 3)
                throw new InvalidOperationException("The public app did not keep moving while private content was active.");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Passed = true, PrivateSwitches = checks, PublicCaptureStarts = capture.PublicCaptureStartCount,
                DistinctPublicFrames = publicFrameSignatures.Count, Recording = file.Path,
                Bytes = (await file.GetBasicPropertiesAsync()).Size
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = false, Error = exception.ToString() },
                new JsonSerializerOptions { WriteIndented = true }));
        }
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
