using Clario_Presenter.Capture;
using Clario_Presenter.Camera;
using Clario_Presenter.Services;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

var desktop = new DesktopCompositionLayout(new(0, 0, 1920, 1080), new(0, 0, 1920, 1040));
Check(desktop.TaskbarBounds == new DisplayBounds(0, 1040, 1920, 40), "Bottom taskbar is reserved.");
var dialog = new DisplayBounds(800, 60, 600, 850);
Check(desktop.ClipPublicWindow(dialog) == dialog, "A small dialog keeps its original desktop geometry.");
var mappedDialog = desktop.MapRegion(dialog, 0, 0, 1536, 864);
Check(mappedDialog == (640d, 48d, 480d, 680d), "A CLI dialog must not be enlarged to the full output.");
Check(desktop.MapRegion(desktop.TaskbarBounds, 0, 0, 1536, 864) == (0d, 832d, 1536d, 32d),
    "The taskbar occupies the same scaled strip while protected windows are active.");
Check(desktop.ClipPublicWindow(new(0, 0, 1920, 1080)) == new DisplayBounds(0, 0, 1920, 1040),
    "Fullscreen windows cannot overwrite the taskbar.");
Check(desktop.ClipPublicWindow(new(-50, -20, 300, 200)) == new DisplayBounds(0, 0, 250, 180),
    "Partially offscreen windows are clipped rather than recentered.");
Check(desktop.ClipPublicWindow(new(2200, 0, 300, 200)).Width == 0,
    "A public window on another monitor cannot appear in the source desktop.");
var leftMonitor = new DesktopCompositionLayout(new(-1920, -100, 1920, 1080), new(-1920, -100, 1920, 1040));
Check(leftMonitor.MapRegion(new(-1120, -40, 600, 850), 0, 0, 1536, 864) == mappedDialog,
    "Negative monitor origins produce identical local placements.");
var sideTaskbar = new DesktopCompositionLayout(new(0, 0, 1920, 1080), new(48, 0, 1872, 1080));
Check(sideTaskbar.TaskbarBounds == new DisplayBounds(0, 0, 48, 1080), "Side taskbars remain reserved.");
var letterboxDialog = desktop.MapRegion(dialog, 0, 96, 1024, 576);
Check(Math.Abs(letterboxDialog.Y - 128) < 0.001 && Math.Abs(letterboxDialog.Width - 320) < 0.001,
    "Letterboxing changes neither window proportions nor its position within the desktop.");

if (args.Contains("--native"))
{
    foreach (var window in WindowCatalogService.GetPresentableWindows().Where(w =>
        w.ProcessName.Equals("PacketTracer", StringComparison.OrdinalIgnoreCase)))
    {
        var family = WindowCatalogService.GetPublicWindowFamily(window.Handle, []);
        Check(family.Contains(window.Handle), "Visible Cisco windows belong to their captured family.");
        Check(WindowCatalogService.GetPublicWindowFamily(window.Handle, [window.Handle]).Count == 0,
            "A private window family cannot be captured as public.");
        Check(WindowCatalogService.TryGetCaptureBounds(window.Handle, out var bounds)
            && bounds.Width > 0 && bounds.Height > 0, "Cisco capture bounds are valid.");
        Console.WriteLine($"Cisco window family: {family.Count} layer(s), bounds {bounds.Width}x{bounds.Height}.");
    }
}
var cameraOptions = new CameraOverlayOptions { Enabled = true, WidthPercent = 24 };
var bottomRight = CameraOverlayLayout.GetBounds(1920, 1080, cameraOptions);
Check(Math.Abs(bottomRight.Width - 460.8) < 0.001, "Medium camera uses 24% of video width.");
Check(bottomRight.Y + bottomRight.Height <= 1016.001, "Camera leaves the taskbar and bottom safe area clear.");
foreach (var position in Enum.GetValues<CameraOverlayPosition>())
{
    var bounds = CameraOverlayLayout.GetBounds(1920, 1080, cameraOptions with { Position = position });
    Check(bounds.X >= 32 && bounds.Y >= 32 && bounds.X + bounds.Width <= 1888.001
        && bounds.Y + bounds.Height <= 1016.001, $"{position} stays within safe margins.");
    Check(Math.Abs(bounds.Width / bounds.Height - 16d / 9) < 0.001,
        $"{position} preserves the overlay aspect ratio.");
}
var small = CameraOverlayLayout.GetBounds(1920, 1080, cameraOptions with { WidthPercent = -10 });
var large = CameraOverlayLayout.GetBounds(1920, 1080, cameraOptions with { WidthPercent = 200 });
Check(small.Width == 288 && large.Width == 672, "Out-of-range camera sizes are clamped.");
var fourByThree = CameraOverlayLayout.Fit(640, 480, bottomRight);
Check(Math.Abs(fourByThree.Width / fourByThree.Height - 4d / 3) < 0.001
    && fourByThree.X > bottomRight.X && fourByThree.Width < bottomRight.Width,
    "A 4:3 webcam is fitted without stretching or cropping the face.");
var portrait = CameraOverlayLayout.Fit(1080, 1920, bottomRight);
Check(Math.Abs(portrait.Width / portrait.Height - 9d / 16) < 0.001
    && portrait.Width < bottomRight.Width, "A portrait phone camera preserves its full image.");
Check(CameraOverlayLayout.GetBounds(0, 1080, cameraOptions) == default, "Zero-sized previews do not create invalid rectangles.");
var scaledCamera = CameraOverlayLayout.GetBounds(960, 540, cameraOptions);
Check(Math.Abs(scaledCamera.X * 2 - bottomRight.X) < 0.001
    && Math.Abs(scaledCamera.Y * 2 - bottomRight.Y) < 0.001, "Camera placement scales consistently with the output.");
Console.WriteLine($"PASS: {checks} desktop and camera composition checks.");
var publicScene = DesktopWindowPlan.Select(new[]
{
    new DesktopWindowCandidate(1, new(0, 0, 800, 1040)),
    new DesktopWindowCandidate(2, new(800, 0, 1120, 1040)),
    new DesktopWindowCandidate(3, new(0, 0, 1920, 1040)),
    new DesktopWindowCandidate(4, new(2000, 0, 640, 480))
}, new(0, 0, 1920, 1040));
Check(publicScene.Select(window => window.Handle).SequenceEqual(new nint[] { 3, 2, 1 }),
    "Public apps preserve desktop z-order and windows on monitor 2 are excluded.");
var covered = DesktopWindowPlan.Select(new[]
{
    new DesktopWindowCandidate(1, new(0, 0, 1920, 1040)),
    new DesktopWindowCandidate(2, new(300, 200, 640, 480))
}, new(0, 0, 1920, 1040));
Check(covered.Count == 1 && covered[0].Handle == 1, "Fully covered public windows do not create unnecessary GPU captures.");
var secondMonitorScene = DesktopWindowPlan.Select(new[]
{
    new DesktopWindowCandidate(1, new(0, 0, 1920, 1040)),
    new DesktopWindowCandidate(2, new(-1920, 0, 1920, 1040))
}, new(-1920, 0, 1920, 1040));
Check(secondMonitorScene.Count == 1 && secondMonitorScene[0].Handle == 2,
    "Choosing the left/second monitor does not leak the primary monitor into the recording.");
Check(DesktopWindowPlan.Intersects(new(1900, 200, 100, 400), new(0, 0, 1920, 1080)),
    "Private windows spanning monitor edges protect the recorded part.");
Check(!DesktopWindowPlan.Intersects(new(1920, 0, 100, 400), new(0, 0, 1920, 1080)),
    "A window wholly on monitor 2 does not pause monitor 1.");
var boundedScene = DesktopWindowPlan.Select(Enumerable.Range(1, 20)
    .Select(index => new DesktopWindowCandidate(index, new(index * 10, index * 10, 200, 200))),
    new(0, 0, 1920, 1040));
Check(boundedScene.Count == 8, "Many open apps do not allocate unlimited active capture layers.");
Console.WriteLine($"PASS: {checks} total composition and monitor-isolation checks.");
