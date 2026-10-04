using System.Runtime.InteropServices;

namespace Clario_Presenter.Services;

public readonly record struct DisplayBounds(int Left, int Top, int Width, int Height);
public sealed record DisplayTarget(
    nint Handle,
    string DeviceName,
    DisplayBounds Bounds,
    uint DpiX,
    uint DpiY,
    uint RawDpiX,
    uint RawDpiY,
    bool IsPrimary,
    int Index)
{
    public int ScalePercent => (int)Math.Round(DpiX / 96d * 100);
    public double? EstimatedDiagonalInches
    {
        get
        {
            if (RawDpiX is < 40 or > 500 || RawDpiY is < 40 or > 500) return null;
            var widthInches = Bounds.Width / (double)RawDpiX;
            var heightInches = Bounds.Height / (double)RawDpiY;
            var diagonal = Math.Sqrt(widthInches * widthInches + heightInches * heightInches);
            return diagonal is >= 8 and <= 100 ? diagonal : null;
        }
    }

    public string DisplayName
    {
        get
        {
            var physicalSize = EstimatedDiagonalInches is double diagonal
                ? $"  ·  ≈{diagonal:0.#}″"
                : string.Empty;
            return $"Display {Index}{(IsPrimary ? " · Utama" : string.Empty)}  ·  {Bounds.Width} × {Bounds.Height}  ·  {ScalePercent}%{physicalSize}";
        }
    }
}

public static class DisplayCatalogService
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect monitorRect, nint data);

    public static IReadOnlyList<DisplayTarget> GetDisplays()
    {
        var found = new List<(nint Handle, MonitorInfoEx Info)>();
        EnumDisplayMonitors(nint.Zero, nint.Zero, (nint monitor, nint hdc, ref Rect rect, nint data) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info)) found.Add((monitor, info));
            return true;
        }, nint.Zero);

        return found.OrderByDescending(item => (item.Info.Flags & MonitorInfoPrimary) != 0)
            .ThenBy(item => item.Info.Monitor.Left)
            .Select((item, index) =>
            {
                var (dpiX, dpiY) = GetMonitorDpi(item.Handle, MonitorDpiType.Effective, (96, 96));
                var (rawDpiX, rawDpiY) = GetMonitorDpi(item.Handle, MonitorDpiType.Raw, (0, 0));
                return new DisplayTarget(
                    item.Handle,
                    item.Info.DeviceName,
                    new DisplayBounds(item.Info.Monitor.Left, item.Info.Monitor.Top,
                        item.Info.Monitor.Right - item.Info.Monitor.Left,
                        item.Info.Monitor.Bottom - item.Info.Monitor.Top),
                    dpiX,
                    dpiY,
                    rawDpiX,
                    rawDpiY,
                    (item.Info.Flags & MonitorInfoPrimary) != 0,
                    index + 1);
            }).ToArray();
    }

    private static (uint X, uint Y) GetMonitorDpi(
        nint monitor,
        MonitorDpiType type,
        (uint X, uint Y) fallback)
    {
        try
        {
            return GetDpiForMonitor(monitor, type, out var dpiX, out var dpiY) >= 0
                ? (dpiX, dpiY)
                : fallback;
        }
        catch (DllNotFoundException)
        {
            return fallback;
        }
        catch (EntryPointNotFoundException)
        {
            return fallback;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    private enum MonitorDpiType
    {
        Effective = 0,
        Raw = 2
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);
}
