using Clario_Presenter.Capture;
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
Console.WriteLine($"PASS: {checks} desktop composition checks.");
