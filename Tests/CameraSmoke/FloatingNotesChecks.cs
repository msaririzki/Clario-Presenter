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
            notes.SetDocument(NoteDocument.Parse("## Router\n**PRIVATE TEST**\n\n```\nshow ip interface brief\n```\n## Switch\nVLAN", true), true);
            notes.Show(display);
            if (!notes.IsShowing) throw new InvalidOperationException(notes.Status);
            var area = notes.Bounds;
            using var router = PatternWindow(area.Left + 15, area.Top + 100, 150, 100, Color.FromArgb(30, 170, 60));
            using var switchDialog = PatternWindow(area.Left + 175, area.Top + 100, 150, 100, Color.FromArgb(45, 90, 214));
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
            var routerPixels = ReadPixel(capture, display.Bounds, routerPoint);
            var switchPixels = ReadPixel(capture, display.Bounds, switchPoint);
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
            // Assert a fresh frame while the notes are visible, not a stale baseline.
            router.BackColor = Color.FromArgb(220, 145, 27);
            switchDialog.BackColor = Color.FromArgb(166, 48, 189);
            notes.Go(1);
            notes.SetAppearance(.45f, 19, 440);
            notes.Show(display);
            await Task.Delay(450);
            if (ReadPixel(capture, display.Bounds, routerPoint) is not { R: > 200, G: > 125, B: < 65 }
                || ReadPixel(capture, display.Bounds, switchPoint) is not { R: > 145, G: < 70, B: > 165 })
                throw new InvalidOperationException("A dialog was hidden or obscured after changing the note and opacity.");

            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                Passed = true, WindowsGraphicsCapture = true, ClickThrough = true,
                FocusPreserved = true, FreshDialogFrames = 2, CaptureExclusion = true
            }));
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = false, Stage = stage, Error = exception.ToString() }));
        }
    }

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
    private static extern nint GetForegroundWindow();
}
