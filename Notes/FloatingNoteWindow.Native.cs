using System.Runtime.InteropServices;

namespace Clario_Presenter.Notes;

internal sealed partial class FloatingNoteWindow
{
    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WndProc Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PaintData
    {
        public nint DeviceContext;
        public int Erase, Left, Top, Right, Bottom, Restore, Update;
        private long _reserved1, _reserved2, _reserved3, _reserved4;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint HeaderSize;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int DpiX, DpiY;
        public uint ColorsUsed, ColorsImportant, Color;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint initialization);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint SetWindowLong(nint window, int index, uint value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")]
    private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint window, out PaintData paint);
    [DllImport("user32.dll")]
    private static extern bool EndPaint(nint window, ref PaintData paint);
    [DllImport("gdi32.dll")]
    private static extern int StretchDIBits(nint dc, int xDest, int yDest, int widthDest, int heightDest,
        int xSrc, int ySrc, int widthSrc, int heightSrc, byte[] pixels, ref BitmapInfo bitmap, uint usage, uint operation);
}
