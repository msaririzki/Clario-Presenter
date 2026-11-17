using System.ComponentModel;
using System.Runtime.InteropServices;
using Clario_Presenter.Services;
using Microsoft.Graphics.Canvas;

namespace Clario_Presenter.Notes;

/// <summary>A local-only top-level window. Its pixels never enter Clario's renderer.</summary>
internal sealed partial class FloatingNoteWindow : IDisposable
{
    private const uint BaseStyles = 0x08080088; // NOACTIVATE | LAYERED | TOOLWINDOW | TOPMOST
    private const uint Transparent = 0x20;
    private const uint ExcludeFromCapture = 0x11;
    private const uint NoActivate = 0x10;
    private readonly WndProc _wndProc;
    private readonly string _className = $"ClarioFloatingNote_{Guid.NewGuid():N}";
    private readonly nint _instance = GetModuleHandle(null);
    private nint _window;
    private bool _shown;
    private bool _requested;
    private bool _dragging;
    private Point _dragOffset;
    private DisplayTarget? _placement;
    private DisplayBounds _bounds;
    private DisplayTarget? _recorded;
    private DisplayTarget? _audience;
    private IReadOnlyList<NoteStep> _steps = [new("Catatan", [])];
    private int _step;
    private float _scale = 1;
    private float _scroll;
    private float _maxScroll;
    private float _opacity = .82f;
    private float _fontSize = 16;
    private float _width = 440;
    private float _height = 360;
    private byte[]? _pixels;
    private int _pixelWidth, _pixelHeight;

    internal event EventHandler? Changed;
    internal bool IsShowing => _shown;
    internal bool IsRequested => _requested;
    internal bool ClickThrough { get; private set; }
    internal bool ExcludedFromCapture { get; private set; }
    internal string AffinityDiagnostic { get; private set; } = "";
    internal bool ShortcutsAvailable { get; }
    internal string Status { get; private set; } = "";
    internal nint Handle => _window;
    internal DisplayBounds Bounds => _bounds;

    internal FloatingNoteWindow(bool registerShortcuts = true)
    {
        _wndProc = WindowMessage;
        var windowClass = new WindowClass
        {
            Instance = _instance, ClassName = _className,
            Procedure = _wndProc, Cursor = LoadCursor(0, 32512)
        };
        if (RegisterClass(ref windowClass) == 0) throw new Win32Exception();
        _window = CreateWindowEx(BaseStyles, _className, "Catatan mengambang · Clario",
            0x80000000, 0, 0, 1, 1, 0, 0, _instance, 0);
        if (_window == 0)
        {
            var error = new Win32Exception();
            UnregisterClass(_className, _instance);
            throw error;
        }
        // Use DWM's redirected layered window. UpdateLayeredWindow bypasses the
        // redirection surface and rejects display affinity on some Windows builds.
        if (!SetLayeredWindowAttributes(_window, 0, 255, 2))
        {
            var error = new Win32Exception();
            Dispose();
            throw error;
        }
        ShortcutsAvailable = registerShortcuts && RegisterShortcuts();
    }

    internal void SetDocument(IReadOnlyList<NoteStep> steps, bool reset)
    {
        _steps = steps;
        _step = reset ? 0 : Math.Clamp(_step, 0, _steps.Count - 1);
        if (reset) _scroll = 0;
        if (_shown) Refresh();
    }

    internal void SetAppearance(float opacity, float fontSize, float width)
    {
        _opacity = opacity;
        _fontSize = fontSize;
        _width = width;
        _height = width * .8f;
    }

    internal void ApplyMonitors(DisplayTarget? preferred, DisplayTarget? recorded, DisplayTarget? audience)
    {
        _recorded = recorded;
        _audience = audience;
        if (_requested) Show(preferred);
    }

    internal void Toggle(DisplayTarget? preferred)
    {
        if (_requested) Hide();
        else Show(preferred);
    }

    internal void Show(DisplayTarget? preferred)
    {
        if (_window == 0) return;
        _requested = true;
        if (_shown) HideWindow();
        // Check protection before publishing any new pixels or moving to a captured display.
        ExcludedFromCapture = TryExclude();
        var displays = DisplayCatalogService.GetDisplays();
        var recorded = displays.FirstOrDefault(display => display.DeviceName == _recorded?.DeviceName);
        var audience = displays.FirstOrDefault(display => display.DeviceName == _audience?.DeviceName);
        if ((_recorded is not null && recorded is null) || (_audience is not null && audience is null))
        {
            Status = "Susunan layar berubah. Stop preview atau presentasi sebelum menyematkan kembali.";
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        preferred ??= _placement ?? displays.FirstOrDefault(display => display.IsPrimary);
        var target = displays.OrderByDescending(display => display.DeviceName == preferred?.DeviceName)
            .FirstOrDefault(display => NoteOverlayPlacement.IsAllowed(display.Bounds, ExcludedFromCapture,
                recorded?.Bounds, audience?.Bounds));
        if (target is null)
        {
            HideWindow();
            Status = "Catatan disembunyikan. Tidak ada layar yang aman untuk menampilkannya.";
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _scale = Math.Clamp(target.DpiX / 96f, 1, 3);
        var width = (int)Math.Ceiling(_width * _scale);
        var height = (int)Math.Ceiling(_height * _scale);
        var keepPosition = _placement?.DeviceName == target.DeviceName;
        var requested = new DisplayBounds(
            keepPosition ? _bounds.Left : target.WorkArea.Left + target.WorkArea.Width - width - 24,
            keepPosition ? _bounds.Top : target.WorkArea.Top + 24, width, height);
        _bounds = NoteOverlayPlacement.Clamp(requested, target.WorkArea);
        _placement = target;
        Refresh();
        // Check again after updating the layered surface, before showing the window.
        ExcludedFromCapture = TryExclude();
        if (!NoteOverlayPlacement.IsAllowed(_bounds, ExcludedFromCapture, recorded?.Bounds, audience?.Bounds))
        {
            HideWindow();
            Status = "Catatan disembunyikan. Pilih layar lain untuk catatan.";
        }
        else
        {
            if (!SetWindowPos(_window, -1, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, NoActivate | 0x40))
                throw new Win32Exception();
            _shown = true;
            Status = preferred?.DeviceName == target.DeviceName || preferred is null
                ? $"Catatan disematkan di Layar {target.Index}."
                : $"Catatan dipindah ke Layar {target.Index} agar tidak ikut tampil.";
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Hide()
    {
        _requested = false;
        HideWindow();
        Status = "Catatan mengambang ditutup.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void HideWindow()
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseCapture();
        }
        ShowWindow(_window, 0);
        _shown = false;
    }

    internal void SetClickThrough(bool enabled)
    {
        if (_window == 0 || enabled == ClickThrough) return;
        // GWL_EXSTYLE is a 32-bit value on both x64 and x86.
        if (SetWindowLong(_window, -20, BaseStyles | (enabled ? Transparent : 0)) == 0
            && Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception();
        ClickThrough = enabled;
        if (_shown) Show(_placement);
        else Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Go(int direction)
    {
        var next = Math.Clamp(_step + direction, 0, _steps.Count - 1);
        if (next == _step) return;
        _step = next;
        _scroll = 0;
        if (_shown) Refresh();
    }

    private void Scroll(int direction)
    {
        _scroll = Math.Clamp(_scroll + direction * 58, 0, _maxScroll);
        if (_shown) Refresh();
    }

    private bool TryExclude()
    {
        if (!SetWindowDisplayAffinity(_window, ExcludeFromCapture))
        {
            AffinityDiagnostic = $"Set={Marshal.GetLastWin32Error()}";
            return false;
        }
        var read = GetWindowDisplayAffinity(_window, out var actual);
        AffinityDiagnostic = read ? $"Affinity=0x{actual:X}" : $"Get={Marshal.GetLastWin32Error()}";
        return read && actual == ExcludeFromCapture;
    }

    private void Refresh()
    {
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, _bounds.Width / _scale, _bounds.Height / _scale, 96 * _scale);
        using (var session = target.CreateDrawingSession())
        {
            _maxScroll = NoteOverlayRenderer.Draw(device, session, _steps[_step], _step, _steps.Count,
                _bounds.Width / _scale, _bounds.Height / _scale, _fontSize, ClickThrough, _scroll);
            _scroll = Math.Min(_scroll, _maxScroll);
        }
        _pixels = target.GetPixelBytes();
        _pixelWidth = (int)target.SizeInPixels.Width;
        _pixelHeight = (int)target.SizeInPixels.Height;
        if (!SetLayeredWindowAttributes(_window, 0, (byte)Math.Round(Math.Clamp(_opacity, .25f, 1) * 255), 2))
            throw new Win32Exception();
        if (!KeepVisibleIfProtected()) return;
        InvalidateRect(_window, 0, false);
    }

    private void Paint(nint hwnd)
    {
        var dc = BeginPaint(hwnd, out var paint);
        try
        {
            if (dc == 0 || _pixels is null) return;
            if (!KeepVisibleIfProtected()) return;
            var info = new BitmapInfo { HeaderSize = 40, Width = _pixelWidth, Height = -_pixelHeight, Planes = 1, Bits = 32 };
            StretchDIBits(dc, 0, 0, _bounds.Width, _bounds.Height, 0, 0, _pixelWidth, _pixelHeight,
                _pixels, ref info, 0, 0x00CC0020);
        }
        finally { EndPaint(hwnd, ref paint); }
    }

    private bool KeepVisibleIfProtected()
    {
        if (!_shown || !ExcludedFromCapture
            || (GetWindowDisplayAffinity(_window, out var actual) && actual == ExcludeFromCapture)) return true;
        HideWindow();
        _requested = false;
        ExcludedFromCapture = false;
        Status = "Catatan disembunyikan karena pengecualian rekaman hilang. Sematkan kembali.";
        Changed?.Invoke(this, EventArgs.Empty);
        return false;
    }

    private bool RegisterShortcuts()
    {
        uint[] keys = [0x77, 0x78, 0x79, 0x7A, 0x26, 0x28]; // F8–F11, Up, Down
        var success = true;
        for (var i = 0; i < keys.Length; i++)
            success &= RegisterHotKey(_window, i + 1, 0x4006, keys[i]); // Ctrl+Shift, no repeat
        return success;
    }

    private nint WindowMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case 0xF: Paint(hwnd); return 0; // WM_PAINT
                case 0x10: Hide(); return 0; // WM_CLOSE: keep shortcuts available.
                case 0x21: return 3; // MA_NOACTIVATE
                case 0x7E: // WM_DISPLAYCHANGE
                    if (_shown)
                    {
                        HideWindow();
                        _requested = false;
                        Status = "Susunan layar berubah. Sematkan kembali catatan.";
                        Changed?.Invoke(this, EventArgs.Empty);
                    }
                    return 0;
                case 0x312:
                    if (wParam == 1) Toggle(_placement);
                    else if (_shown)
                    {
                        switch (wParam)
                        {
                            case 2: SetClickThrough(!ClickThrough); break;
                            case 3: Go(-1); break;
                            case 4: Go(1); break;
                            case 5: Scroll(-1); break;
                            case 6: Scroll(1); break;
                        }
                    }
                    return 0;
                case 0x201 when !ClickThrough && _shown:
                    var x = (short)((long)lParam & 0xffff);
                    var y = (short)(((long)lParam >> 16) & 0xffff);
                    if (y > _bounds.Height - NoteOverlayRenderer.Footer * _scale)
                    {
                        if (x < 120 * _scale) Go(-1);
                        if (x > _bounds.Width - 120 * _scale) Go(1);
                    }
                    else if (y < NoteOverlayRenderer.Header * _scale)
                    {
                        _dragging = true;
                        _dragOffset = new Point { X = x, Y = y };
                        SetCapture(hwnd);
                    }
                    return 0;
                case 0x200 when _dragging && _placement is not null:
                    if (GetCursorPos(out var cursor))
                    {
                        _bounds = NoteOverlayPlacement.Clamp(_bounds with
                        {
                            Left = cursor.X - _dragOffset.X,
                            Top = cursor.Y - _dragOffset.Y
                        }, _placement.WorkArea);
                        SetWindowPos(hwnd, -1, _bounds.Left, _bounds.Top, 0, 0, NoActivate | 1);
                    }
                    return 0;
                case 0x202 when _dragging:
                    _dragging = false;
                    ReleaseCapture();
                    return 0;
                case 0x215: _dragging = false; break; // WM_CAPTURECHANGED
                case 0x20A when !ClickThrough && _shown:
                    Scroll(Math.Sign(-(short)(((long)wParam >> 16) & 0xffff)));
                    return 0;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            Hide();
            return 0;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_window == 0) return;
        HideWindow();
        for (var i = 1; i <= 6; i++) UnregisterHotKey(_window, i);
        DestroyWindow(_window);
        _window = 0;
        UnregisterClass(_className, _instance);
    }
}
