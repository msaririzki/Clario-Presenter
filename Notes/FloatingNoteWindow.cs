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
    private nint _footerInput;
    private bool _shown;
    private bool _requested;
    private DisplayTarget? _placement;
    private DisplayBounds _bounds;
    private DisplayTarget? _recorded;
    private DisplayTarget? _audience;
    private IReadOnlyList<NoteStep> _steps = [new("Catatan", [])];
    private int _step;
    private float _scale = 1;
    private float _scroll;
    private float _maxScroll;
    private float _contentHeight;
    private bool _indexOpen;
    private float _indexScroll;
    private readonly NoteOverlayRenderer _renderer = new();
    private CanvasRenderTarget? _renderTarget;
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
    internal (float Width, float Height) Size => (_width, _height);
    internal int StepIndex => _step;
    internal float ScrollOffset => _indexOpen ? _indexScroll : _scroll;

    private NoteOverlayGeometry Geometry => new(_bounds.Width / _scale, _bounds.Height / _scale);

    internal FloatingNoteWindow(bool registerShortcuts = true)
    {
        _wndProc = WindowMessage;
        var windowClass = new WindowClass
        {
            Instance = _instance, ClassName = _className,
            Procedure = _wndProc, Cursor = LoadCursor(0, 32512), Background = 6 // COLOR_WINDOW + 1
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
        // WS_EX_TRANSPARENT passes the entire layered window through, even across processes.
        // A nearly transparent, owned strip handles just the footer. It never paints note text.
        _footerInput = CreateWindowEx(BaseStyles, _className, "", 0x80000000, 0, 0, 1, 1, _window, 0, _instance, 0);
        if (_footerInput == 0 || !SetLayeredWindowAttributes(_footerInput, 0, 1, 2))
        {
            var error = new Win32Exception();
            Dispose();
            throw error;
        }
        SetWindowDisplayAffinity(_footerInput, ExcludeFromCapture);
        ShortcutsAvailable = registerShortcuts && RegisterShortcuts();
    }

    internal void SetDocument(IReadOnlyList<NoteStep> steps, bool reset)
    {
        _steps = steps;
        _step = reset ? 0 : Math.Clamp(_step, 0, _steps.Count - 1);
        if (reset || _steps.Count < 2)
        {
            _indexOpen = false;
            _indexScroll = 0;
        }
        if (reset) _scroll = 0;
        if (_shown)
        {
            Refresh();
            UpdateInputMode();
        }
    }

    internal void SetAppearance(float opacity, float fontSize, float width, float height)
    {
        _opacity = opacity;
        _fontSize = fontSize;
        _width = width;
        _height = height;
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
            UpdateInputMode();
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
        _shown = false;
        if (_footerInput != 0) ShowWindow(_footerInput, 0);
        CancelResizeRender();
        EndPointer(release: true);
        ShowWindow(_window, 0);
    }

    internal void SetClickThrough(bool enabled)
    {
        if (_window == 0 || enabled == ClickThrough) return;
        ClickThrough = enabled;
        UpdateInputMode();
        if (_shown) Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private int FooterTop => (int)Math.Floor(_bounds.Height - NoteOverlayGeometry.FooterHeight * _scale);

    private void UpdateInputMode()
    {
        var passThrough = ClickThrough && !_indexOpen;
        if (SetWindowLong(_window, -20, BaseStyles | (passThrough ? Transparent : 0)) == 0
            && Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception();
        if (!_shown || !passThrough)
            ShowWindow(_footerInput, 0);
        else if (!SetWindowPos(_footerInput, -1, _bounds.Left, _bounds.Top + FooterTop, _bounds.Width,
            _bounds.Height - FooterTop, NoActivate | 0x40))
            throw new Win32Exception();
    }

    internal void Go(int direction) => SelectStep(Math.Clamp(_step + direction, 0, _steps.Count - 1));

    private void SelectStep(int index)
    {
        if (!_shown) return;
        _step = index;
        _indexOpen = false;
        _scroll = _renderer.SectionTop(index);
        Refresh();
        UpdateInputMode();
    }

    private void ToggleIndex()
    {
        if (_steps.Count < 2) return;
        _indexOpen = !_indexOpen;
        if (_indexOpen)
        {
            var row = _step * NoteOverlayGeometry.IndexRowHeight;
            _indexScroll = Math.Max(0, row - (float)Geometry.Body.Height / 3);
        }
        Refresh();
        UpdateInputMode();
    }

    private void ScrollTo(float offset)
    {
        offset = Math.Clamp(offset, 0, _maxScroll);
        if (offset == ScrollOffset) return;
        if (_indexOpen) _indexScroll = offset;
        else
        {
            _scroll = offset;
            _step = _renderer.SectionAt(offset);
        }
        Refresh();
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
        if (!KeepVisibleIfProtected()) return;
        var device = CanvasDevice.GetSharedDevice();
        var geometry = Geometry;
        var documentHeight = _renderer.Measure(device, _steps, (float)geometry.Body.Width, _fontSize, ref _scroll);
        _scroll = Math.Clamp(_scroll, 0, geometry.ScrollLimit(documentHeight));
        var sectionTop = _renderer.SectionTop(_step);
        if (sectionTop < _scroll || sectionTop >= _scroll + geometry.Body.Height)
            _step = _renderer.SectionAt(_scroll);
        _contentHeight = _indexOpen ? _steps.Count * NoteOverlayGeometry.IndexRowHeight : documentHeight;
        _maxScroll = geometry.ScrollLimit(_contentHeight);
        if (_indexOpen) _indexScroll = Math.Clamp(_indexScroll, 0, _maxScroll);

        if (_renderTarget is null || _renderTarget.SizeInPixels.Width != _bounds.Width
            || _renderTarget.SizeInPixels.Height != _bounds.Height || _renderTarget.Dpi != 96 * _scale
            || !ReferenceEquals(_renderTarget.Device, device))
        {
            _renderTarget?.Dispose();
            _renderTarget = new CanvasRenderTarget(device, geometry.Width, geometry.Height, 96 * _scale);
        }
        using (var session = _renderTarget.CreateDrawingSession())
            _renderer.Draw(session, geometry, _steps, _step, _indexOpen, ClickThrough, ScrollOffset, _contentHeight);

        _pixels = _renderTarget.GetPixelBytes();
        _pixelWidth = (int)_renderTarget.SizeInPixels.Width;
        _pixelHeight = (int)_renderTarget.SizeInPixels.Height;
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

    private nint WindowMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (hwnd == _footerInput)
            {
                switch (message)
                {
                    case 0x21: return 3; // MA_NOACTIVATE
                    case 0x201 when _shown && ClickThrough && !_indexOpen:
                        var location = MousePoint(lParam);
                        location.Y += FooterTop;
                        ActivateFooter(LogicalPoint(location));
                        return 0;
                    case 0x20A when _shown:
                        ScrollWheel(wParam);
                        return 0;
                }
                return DefWindowProc(hwnd, message, wParam, lParam);
            }
            switch (message)
            {
                case 0xF: Paint(hwnd); return 0; // WM_PAINT
                case 0x14: return 1; // WM_ERASEBKGND: Paint covers the panel, including during resize.
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
                case 0x312: HandleShortcut((int)wParam); return 0; // WM_HOTKEY
                case 0x113 when wParam == ResizeTimer: // WM_TIMER
                    CancelResizeRender();
                    if (_shown) Refresh();
                    return 0;
                case 0x113 when wParam == PointerTimer:
                    TrackPointer();
                    return 0;
                case 0x20 when (!ClickThrough || _indexOpen) && _shown && (short)lParam == 1: // WM_SETCURSOR, HTCLIENT
                    UpdatePointerCursor();
                    return 1;
                case 0x201 when (!ClickThrough || _indexOpen) && _shown: // WM_LBUTTONDOWN
                    BeginPointer(hwnd, MousePoint(lParam));
                    return 0;
                case 0x200 when _pointerAction != PointerAction.None: // WM_MOUSEMOVE
                    DragPointer(MousePoint(lParam));
                    return 0;
                case 0x202 when _pointerAction != PointerAction.None: // WM_LBUTTONUP
                    EndPointer(release: true);
                    return 0;
                case 0x215 when !_trackingPointer: EndPointer(release: false); break; // WM_CAPTURECHANGED
                case 0x20A when (!ClickThrough || _indexOpen) && _shown: // WM_MOUSEWHEEL
                    ScrollWheel(wParam);
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
        for (var i = 1; i <= ShortcutKeys.Length; i++) UnregisterHotKey(_window, i);
        _renderer.Dispose();
        _renderTarget?.Dispose();
        _pixels = null;
        if (_footerInput != 0) DestroyWindow(_footerInput);
        _footerInput = 0;
        DestroyWindow(_window);
        _window = 0;
        UnregisterClass(_className, _instance);
    }
}
