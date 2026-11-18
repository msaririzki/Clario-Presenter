using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Clario_Presenter.Capture;
using Clario_Presenter.Notes;
using Clario_Presenter.Services;
using Microsoft.Graphics.Canvas;

namespace Clario_Presenter;

internal static class FloatingNotesChecks
{
    public static async Task RunAsync(string reportPath)
    {
        var stage = "setup";
        try
        {
            var display = DisplayCatalogService.GetDisplays().First(item => item.IsPrimary);
            using var notes = new FloatingNoteWindow(registerShortcuts: false);
            stage = "long text rendering";
            const string line = "Catatan pengujian Cisco untuk narasi dan perintah router.\n";
            var longBody = string.Concat(Enumerable.Repeat(line, (NoteDocument.MaximumLength - 128) / line.Length));
            AssertTailRendered(marker => NoteDocument.Parse(longBody + marker, false));
            AssertTailRendered(marker => NoteDocument.Parse($"## Router\n\n```\n{longBody}{marker}\n```", true));
            AssertContinuousLayout();

            stage = "navigation and resize";
            CheckNavigation(notes, display, longBody);
            notes.SetAppearance(.82f, 16, 440, 360);
            notes.SetDocument(NoteDocument.Parse("## Router\n**PRIVATE TEST**\n\n```\nshow ip interface brief\n```\n## Switch\nVLAN", true), true);
            notes.Show(display);
            if (!notes.IsShowing) throw new InvalidOperationException(notes.Status);
            var area = notes.Bounds;
            using var router = PatternWindow(area.Left + 15, area.Top + 100, 150, 100, Color.FromArgb(30, 170, 60));
            using var switchDialog = PatternWindow(area.Left + 175, area.Top + 100, 150, area.Height - 100, Color.FromArgb(45, 90, 214));
            notes.Hide();
            router.Show();
            switchDialog.Show();
            using var capture = new CaptureSessionService();
            var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.FrameAvailable += (_, _) => firstFrame.TrySetResult();
            capture.StartMonitor(display.Handle);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));

            stage = "baseline";
            var routerPoint = new NativePoint(area.Left + 75, area.Top + 145);
            var switchPoint = new NativePoint(area.Left + 250, area.Top + 145);
            var footerPoint = new NativePoint(area.Left + 250, area.Top + area.Height - 20);
            var routerPixels = ReadPixel(capture, display.Bounds, routerPoint);
            var switchPixels = ReadPixel(capture, display.Bounds, switchPoint);
            AssertUnchanged(ReadPixel(capture, display.Bounds, footerPoint), switchPixels);
            if (routerPixels is not { G: > 150, B: < 100 } || switchPixels is not { B: > 180, R: < 80 })
                throw new InvalidOperationException($"Expected synthetic dialogs below the panel, got {routerPixels} and {switchPixels}.");

            stage = "exclude and hit test";
            var foreground = GetForegroundWindow();
            notes.ApplyMonitors(display, display, null);
            notes.Show(display);
            if (!notes.IsShowing || !notes.ExcludedFromCapture || notes.Bounds != area)
                throw new InvalidOperationException($"Capture exclusion unavailable on this monitor. {notes.Status} {notes.AffinityDiagnostic}");
            if (WindowFromPoint(routerPoint) != notes.Handle)
                throw new InvalidOperationException("Atur mode does not receive mouse input above the router.");
            notes.SetClickThrough(true);
            if (WindowFromPoint(routerPoint) != router.Handle || WindowFromPoint(switchPoint) != switchDialog.Handle)
                throw new InvalidOperationException("Click-through does not reach both underlying windows.");
            if (GetForegroundWindow() != foreground) throw new InvalidOperationException("The overlay stole keyboard focus.");

            stage = "same capture with visible notes";
            await Task.Delay(450);
            AssertUnchanged(ReadPixel(capture, display.Bounds, routerPoint), routerPixels);
            AssertUnchanged(ReadPixel(capture, display.Bounds, switchPoint), switchPixels);
            AssertUnchanged(ReadPixel(capture, display.Bounds, footerPoint), switchPixels);
            // Assert a fresh frame while the notes are visible, not a stale baseline.
            router.BackColor = Color.FromArgb(220, 145, 27);
            switchDialog.BackColor = Color.FromArgb(166, 48, 189);
            notes.Go(1);
            notes.SetAppearance(.45f, 19, 440, 360);
            notes.Show(display);
            await Task.Delay(450);
            if (ReadPixel(capture, display.Bounds, routerPoint) is not { R: > 200, G: > 125, B: < 65 }
                || ReadPixel(capture, display.Bounds, switchPoint) is not { R: > 145, G: < 70, B: > 165 })
                throw new InvalidOperationException("A dialog was hidden or obscured after changing the note and opacity.");
            AssertUnchanged(ReadPixel(capture, display.Bounds, footerPoint), ReadPixel(capture, display.Bounds, switchPoint));

            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Passed = true, WindowsGraphicsCapture = true, ClickThrough = true,
                FocusPreserved = true, FreshDialogFrames = 2, CaptureExclusion = true,
                LongPlainText = true, LongFencedCode = true, Resizing = true, SectionIndex = true, ScrollShortcuts = true,
                ContinuousLayout = true, ClickThroughFooter = true
            }));
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = false, Stage = stage, Error = exception.ToString() }));
        }
    }

    private static void AssertTailRendered(Func<string, IReadOnlyList<NoteStep>> build)
    {
        using var renderer = new NoteOverlayRenderer();
        var device = CanvasDevice.GetSharedDevice();
        var geometry = new NoteOverlayGeometry(380, 220);
        using var output = new CanvasRenderTarget(device, geometry.Width, geometry.Height, 96);
        byte[] RenderTail(string marker)
        {
            var document = build(marker);
            var scroll = 0f;
            var height = renderer.Measure(device, document, (float)geometry.Body.Width, 16, ref scroll);
            if (height < 30_000) throw new InvalidOperationException($"Long content was measured as only {height}.");
            using (var session = output.CreateDrawingSession())
                renderer.Draw(session, geometry, document, 0, false, false, geometry.ScrollLimit(height), height);
            var body = geometry.Body;
            return output.GetPixelBytes((int)body.X, (int)body.Y, (int)body.Width, (int)body.Height);
        }

        if (RenderTail("END WWWWWWWW").AsSpan().SequenceEqual(RenderTail("END iiiiiiii")))
            throw new InvalidOperationException("Changing the final line did not change the rendered bottom of the note.");
    }

    private static void AssertContinuousLayout()
    {
        var device = CanvasDevice.GetSharedDevice();
        using var renderer = new NoteOverlayRenderer();
        const string commands = "show ip interface brief  show running-config  show interfaces status";
        var document = NoteDocument.Parse($"## Router\n{commands}\n## Switch\n{commands}\n## Selesai\nPeriksa hasil", true);
        var shortPanel = new NoteOverlayGeometry(270, 168);
        var tallPanel = new NoteOverlayGeometry(270, 530);
        var scroll = 0f;
        var narrowHeight = renderer.Measure(device, document, (float)shortPanel.Body.Width, 16, ref scroll);
        var second = renderer.SectionTop(1);
        if (second < shortPanel.Body.Height || second + 24 >= tallPanel.Body.Height || renderer.SectionAt(second) != 1)
            throw new InvalidOperationException("The next section should enter the expanded viewport without navigation.");
        using var image = new CanvasRenderTarget(device, tallPanel.Width, tallPanel.Height, 96);
        using (var drawing = image.CreateDrawingSession())
            renderer.Draw(drawing, tallPanel, document, 0, false, true, 0, narrowHeight);
        var pixels = image.GetPixelBytes((int)tallPanel.Body.X, (int)(tallPanel.Body.Y + second),
            (int)tallPanel.Body.Width, 24);
        if (!Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4 + 2] > 150))
            throw new InvalidOperationException("The next section was measured but not drawn in the extra space.");
        scroll = second;
        var wideHeight = renderer.Measure(device, document, 650, 16, ref scroll);
        if (wideHeight >= narrowHeight || renderer.SectionTop(1) >= second || Math.Abs(scroll - renderer.SectionTop(1)) > 1)
            throw new InvalidOperationException("Widening the note failed to rewrap text at the current reading position.");
    }

    private static void CheckNavigation(FloatingNoteWindow notes, DisplayTarget display, string longBody)
    {
        notes.SetDocument(NoteDocument.Parse(longBody, false), true);
        notes.Show(display);
        var foreground = GetForegroundWindow();
        notes.SetClickThrough(true);
        Hotkey(notes, 8); // Page Down
        var paged = notes.ScrollOffset;
        Hotkey(notes, 10); // End
        if (paged <= 0 || notes.ScrollOffset < 30_000)
            throw new InvalidOperationException("The long note could not be read with global scroll controls.");
        Hotkey(notes, 9); // Home
        if (notes.ScrollOffset != 0 || GetForegroundWindow() != foreground)
            throw new InvalidOperationException("Scroll controls did not return to the top without taking focus.");
        notes.SetClickThrough(false);

        var scale = Math.Clamp(display.DpiX / 96f, 1, 3);
        var bounds = notes.Bounds;
        var y = bounds.Height - 5;
        SendMessage(notes.Handle, 0x201, 1, PointParameter(5, y));
        SendMessage(notes.Handle, 0x200, 1, PointParameter(-85, y + 75));
        SendMessage(notes.Handle, 0x202, 0, 0);
        if (notes.Bounds.Width <= bounds.Width || notes.Bounds.Height <= bounds.Height || GetForegroundWindow() != foreground)
            throw new InvalidOperationException("Dragging the resize grip did not change both dimensions without stealing focus.");

        var geometry = new NoteOverlayGeometry(notes.Bounds.Width / scale, notes.Bounds.Height / scale);
        void Click(Windows.Foundation.Point point)
        {
            SendMessage(notes.Handle, 0x201, 1, PointParameter((int)(point.X * scale), (int)(point.Y * scale)));
            SendMessage(notes.Handle, 0x202, 0, 0);
        }

        var scrollX = (int)((geometry.Track.X + 6) * scale);
        SendMessage(notes.Handle, 0x201, 1, PointParameter(scrollX, (int)((geometry.Track.Y + 6) * scale)));
        SendMessage(notes.Handle, 0x200, 1, PointParameter(scrollX, (int)((geometry.Track.Bottom + 4) * scale)));
        SendMessage(notes.Handle, 0x202, 0, 0);
        if (notes.ScrollOffset < 30_000) throw new InvalidOperationException("The scrollbar could not reach the end.");

        var sections = string.Join("\n", Enumerable.Range(1, 80).Select(index => $"## Bagian {index}\nIsi {index}\n"));
        notes.SetDocument(NoteDocument.Parse(sections, true), true);
        notes.SetClickThrough(true);
        void ClickFooter(Windows.Foundation.Rect button)
        {
            var point = new NativePoint(notes.Bounds.Left + (int)((button.X + button.Width / 2) * scale),
                notes.Bounds.Top + (int)((button.Y + button.Height / 2) * scale));
            var target = WindowFromPoint(point);
            if (target != notes.Handle && (target == 0 || GetWindow(target, 4) != notes.Handle))
                throw new InvalidOperationException("The visible footer was not clickable.");
            if (!ScreenToClient(target, ref point)) throw new InvalidOperationException("Footer hit test failed.");
            SendMessage(target, 0x201, 1, PointParameter(point.X, point.Y));
            SendMessage(target, 0x202, 0, PointParameter(point.X, point.Y));
        }
        ClickFooter(geometry.Next);
        if (notes.ScrollOffset <= 0 || notes.StepIndex == 0) throw new InvalidOperationException("Turun failed in Click-through mode.");
        ClickFooter(geometry.Previous);
        if (notes.ScrollOffset != 0) throw new InvalidOperationException("Naik failed in Click-through mode.");
        var wheelPoint = new NativePoint(notes.Bounds.Left + 25, notes.Bounds.Top + notes.Bounds.Height - 20);
        SendMessage(WindowFromPoint(wheelPoint), 0x20A, (nint)(-120 << 16), PointParameter(wheelPoint.X, wheelPoint.Y));
        if (notes.ScrollOffset <= 0) throw new InvalidOperationException("The wheel over the footer did not scroll the note.");
        ClickFooter(geometry.Mode);
        if (notes.ClickThrough) throw new InvalidOperationException("Atur failed.");
        ClickFooter(geometry.Mode);
        if (!notes.ClickThrough) throw new InvalidOperationException("Selesai failed.");
        ClickFooter(geometry.Index);
        for (var page = 0; page < 12; page++) ClickFooter(geometry.Next);
        Click(new(geometry.Body.X + 30, geometry.Body.Bottom - 12));
        if (notes.StepIndex != 79) throw new InvalidOperationException("The final section could not be selected from the index.");
        if (!notes.ClickThrough || GetForegroundWindow() != foreground)
            throw new InvalidOperationException("Mouse navigation changed keyboard focus or the chosen click-through mode.");
        notes.SetClickThrough(false);
    }

    private static nint PointParameter(int x, int y) => (nint)((y << 16) | (x & 0xffff));
    private static void Hotkey(FloatingNoteWindow notes, int id) => SendMessage(notes.Handle, 0x312, id, 0);

    private static Form PatternWindow(int x, int y, int width, int height, Color color) => new()
    {
        Text = "Clario · Uji catatan", StartPosition = FormStartPosition.Manual,
        FormBorderStyle = FormBorderStyle.None, Bounds = new Rectangle(x, y, width, height),
        TopMost = true, BackColor = color
    };

    private static (int R, int G, int B) ReadPixel(CaptureSessionService capture, DisplayBounds monitor, NativePoint point)
    {
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), monitor.Width, monitor.Height, 96);
        using (var session = target.CreateDrawingSession())
            capture.DrawRecordingFrame(session, monitor.Width, monitor.Height);
        var pixels = target.GetPixelBytes(point.X - monitor.Left, point.Y - monitor.Top, 1, 1);
        return (pixels[2], pixels[1], pixels[0]);
    }

    private static void AssertUnchanged((int R, int G, int B) actual, (int R, int G, int B) baseline)
    {
        if (Math.Abs(actual.R - baseline.R) > 5 || Math.Abs(actual.G - baseline.G) > 5 || Math.Abs(actual.B - baseline.B) > 5)
            throw new InvalidOperationException($"Note changed the recorded pixel from {baseline} to {actual}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
