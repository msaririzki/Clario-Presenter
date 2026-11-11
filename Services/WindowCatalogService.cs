using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Clario_Presenter.Capture;

namespace Clario_Presenter.Services;

public sealed record WindowSource(nint Handle, string Title, string ProcessName)
{
    public string DisplayName => $"{Title}  ·  {ProcessName}";
    public string ShortTitle => Title.Length <= 42 ? Title : $"{Title[..39]}…";
}

public static class WindowCatalogService
{
    private const int SwMinimize = 6;
    private const int SwShowNoActivate = 4;
    private const int SwRestore = 9;
    private const int SwMaximize = 3;
    private const uint MonitorDefaultToNull = 0;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpAsyncWindowPos = 0x4000;
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    public static IReadOnlyList<WindowSource> GetPresentableWindows()
    {
        var currentProcessId = Environment.ProcessId;
        var windows = new List<WindowSource>();

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var length = GetWindowTextLength(handle);
            if (length == 0) return true;

            var titleBuilder = new StringBuilder(length + 1);
            GetWindowText(handle, titleBuilder, titleBuilder.Capacity);
            var title = titleBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0 || processId == currentProcessId) return true;

            try
            {
                using var process = Process.GetProcessById((int)processId);
                var processName = process.ProcessName;
                if (!processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
                    && !title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase))
                {
                    windows.Add(new WindowSource(handle, title, processName));
                }
            }
            catch
            {
                // A process can close while the list is being refreshed.
            }
            return true;
        }, nint.Zero);

        return windows.GroupBy(window => window.Handle).Select(group => group.First())
            .OrderBy(window => window.ProcessName).ThenBy(window => window.Title).ToArray();
    }

    public static string? GetForegroundProcessName()
    {
        var handle = GetForegroundWindow();
        if (handle == nint.Zero) return null;

        GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0) return null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public static nint GetForegroundWindowHandle() => GetForegroundWindow();

    public static bool IsApplicationSwitcherActive() => GetAsyncKeyState(0x12) < 0
        || GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0
        || IsWindowVisible(FindWindow("TaskListThumbnailWnd", null))
        || IsWindowVisible(FindWindow("MultitaskingViewFrame", null))
        || IsShellOverlay(GetForegroundWindow());

    public static IReadOnlyList<nint> GetPublicDesktopWindows(DisplayBounds workArea, IEnumerable<nint> privateWindows)
    {
        var candidates = new List<DesktopWindowCandidate>();
        EnumWindows((handle, _) =>
        {
            if (IsWindowCapturable(handle) && !IsShellOverlay(handle)
                && !IsInWindowFamily(handle, privateWindows)
                && TryGetCaptureBounds(handle, out var bounds))
                candidates.Add(new DesktopWindowCandidate(handle, bounds));
            return true;
        }, nint.Zero);
        return DesktopWindowPlan.Select(candidates, workArea).Select(window => window.Handle).ToArray();
    }

    public static bool HasPrivateWindowOnDisplay(DisplayBounds bounds, IEnumerable<nint> privateWindows)
    {
        var found = false;
        EnumWindows((handle, _) =>
        {
            if (IsWindowPresentable(handle) && IsInWindowFamily(handle, privateWindows)
                && TryGetCaptureBounds(handle, out var windowBounds)
                && DesktopWindowPlan.Intersects(bounds, windowBounds)) found = true;
            return !found;
        }, nint.Zero);
        return found;
    }

    private static bool IsShellOverlay(nint handle)
    {
        var className = new StringBuilder(256);
        GetClassName(handle, className, className.Capacity);
        return className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman"
            or "WorkerW" or "MultitaskingViewFrame" or "XamlExplorerHostIslandWindow"
            or "Windows.UI.Core.CoreWindow" or "TaskListThumbnailWnd" or "TaskListOverlayWnd";
    }

    public static nint GetRootOwner(nint handle) => GetAncestor(handle, 3);

    public static bool IsInWindowFamily(nint handle, IEnumerable<nint> selectedWindows)
    {
        if (handle == nint.Zero) return false;
        var root = GetRootOwner(handle);
        return selectedWindows.Any(selected => selected == handle
            || (root != nint.Zero && GetRootOwner(selected) == root));
    }

    public static IReadOnlyList<nint> GetPublicWindowFamily(nint handle, IEnumerable<nint> privateWindows)
    {
        if (!IsWindowCapturable(handle) || IsInWindowFamily(handle, privateWindows)) return [];
        var root = GetRootOwner(handle);
        GetWindowThreadProcessId(handle, out var publicProcessId);
        var windows = new List<nint>();
        // Some apps (including Packet Tracer) use independent top-level windows
        // for device dialogs, with no Win32 owner relationship. Include public
        // windows from that process as well, preserving their desktop z-order.
        EnumWindows((candidate, _) =>
        {
            GetWindowThreadProcessId(candidate, out var candidateProcessId);
            if ((GetRootOwner(candidate) == root || candidateProcessId == publicProcessId)
                && IsWindowCapturable(candidate)
                && !IsInWindowFamily(candidate, privateWindows)) windows.Add(candidate);
            return true;
        }, nint.Zero);
        windows.Reverse();
        return windows;
    }

    public static bool TryGetCaptureBounds(nint handle, out DisplayBounds bounds)
    {
        // GetWindowRect includes invisible resize borders. WGC uses the visible
        // DWM frame, so use those bounds to preserve the original desktop position.
        if (DwmGetWindowAttribute(handle, 9, out var rect, Marshal.SizeOf<Rect>()) < 0
            && !GetWindowRect(handle, out rect))
        {
            bounds = default;
            return false;
        }
        bounds = new DisplayBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public static bool IsRegionCoveredByForeground(DisplayBounds region)
    {
        if (!TryGetCaptureBounds(GetForegroundWindow(), out var foreground)) return false;
        var overlapWidth = Math.Max(0, Math.Min(foreground.Left + foreground.Width, region.Left + region.Width)
            - Math.Max(foreground.Left, region.Left));
        var overlapHeight = Math.Max(0, Math.Min(foreground.Top + foreground.Height, region.Top + region.Height)
            - Math.Max(foreground.Top, region.Top));
        // Maximized resize borders can overlap the taskbar by a few pixels.
        // Only a fullscreen-sized overlap means its safe snapshot must be held.
        return (long)overlapWidth * overlapHeight > (long)region.Width * region.Height * 0.9;
    }

    public static void MinimizeWindows(IEnumerable<nint> windowHandles)
    {
        foreach (var handle in windowHandles) MinimizeWindow(handle);
    }

    public static void MinimizeWindow(nint handle)
    {
        if (IsWindowPresentable(handle)) ShowWindow(handle, SwMinimize);
    }

    public static int MoveWindowsToDisplay(nint sourceMonitor, DisplayBounds destination)
    {
        var moved = 0;
        foreach (var window in GetPresentableWindows())
        {
            if (MonitorFromWindow(window.Handle, MonitorDefaultToNull) != sourceMonitor
                || !GetWindowRect(window.Handle, out var currentBounds))
            {
                continue;
            }

            var wasMaximized = IsZoomed(window.Handle);
            if (wasMaximized) ShowWindow(window.Handle, SwRestore);

            var availableWidth = Math.Max(640, destination.Width - 96);
            var availableHeight = Math.Max(480, destination.Height - 112);
            var width = Math.Clamp(currentBounds.Right - currentBounds.Left, 640, availableWidth);
            var height = Math.Clamp(currentBounds.Bottom - currentBounds.Top, 480, availableHeight);
            var cascade = (moved % 6) * 24;
            var left = destination.Left + 48 + cascade;
            var top = destination.Top + 48 + cascade;

            if (SetWindowPos(window.Handle, nint.Zero, left, top, width, height,
                    SwpNoActivate | SwpNoZOrder | SwpAsyncWindowPos))
            {
                moved++;
                if (wasMaximized) ShowWindow(window.Handle, SwMaximize);
            }
        }

        return moved;
    }

    public static bool IsWindowPresentable(nint handle) =>
        IsWindow(handle) && IsWindowVisible(handle) && !IsIconic(handle)
        && (DwmGetWindowAttributeValue(handle, 14, out var cloaked, sizeof(int)) < 0 || cloaked == 0);

    public static bool IsWindowCapturable(nint handle)
    {
        if (!IsWindowPresentable(handle) || GetWindowTextLength(handle) == 0) return false;
        GetWindowThreadProcessId(handle, out var processId);
        return processId != 0 && processId != Environment.ProcessId;
    }

    public static bool PrepareWindowForBackgroundCapture(nint handle)
    {
        if (!IsWindow(handle) || !IsWindowVisible(handle) || GetWindowTextLength(handle) == 0) return false;
        GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0 || processId == Environment.ProcessId) return false;

        if (IsIconic(handle)) ShowWindow(handle, SwShowNoActivate);
        return IsWindowPresentable(handle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint hWnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out Rect rect);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y,
        int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hWnd, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hWnd, uint attribute, out Rect rect, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeValue(nint hWnd, uint attribute, out int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
