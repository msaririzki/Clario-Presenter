using Clario_Presenter.Services;

namespace Clario_Presenter.Notes;

internal sealed partial class FloatingNoteWindow
{
    private const int ResizeTimer = 1;
    private const int PointerTimer = 2;
    private static readonly uint[] ShortcutKeys = [0x77, 0x78, 0x79, 0x7A, 0x26, 0x28, 0x21, 0x22, 0x24, 0x23];
    private PointerAction _pointerAction;
    private Point _pointerStart;
    private DisplayBounds _dragBounds;
    private NoteResizeEdge _resizeEdges;
    private float _thumbOffset;
    private bool _resizeRenderPending;
    private bool _trackingPointer;

    private enum PointerAction { None, Move, Resize, Scroll }

    private bool RegisterShortcuts()
    {
        var success = true;
        for (var i = 0; i < ShortcutKeys.Length; i++)
            success &= RegisterHotKey(_window, i + 1, 0x4006, ShortcutKeys[i]);
        return success;
    }

    private void HandleShortcut(int id)
    {
        if (id == 1)
        {
            Toggle(_placement);
            return;
        }
        if (!_shown) return;

        var page = Math.Max(_fontSize, (float)Geometry.Body.Height * .9f);
        switch (id)
        {
            case 2: SetClickThrough(!ClickThrough); break;
            case 3: Go(-1); break;
            case 4: Go(1); break;
            case 5: ScrollTo(ScrollOffset - _fontSize * 3.5f); break;
            case 6: ScrollTo(ScrollOffset + _fontSize * 3.5f); break;
            case 7: ScrollTo(ScrollOffset - page); break;
            case 8: ScrollTo(ScrollOffset + page); break;
            case 9: ScrollTo(0); break;
            case 10: ScrollTo(_maxScroll); break;
        }
    }

    private static Point MousePoint(nint value) => new() { X = (short)value, Y = (short)((long)value >> 16) };
    private Windows.Foundation.Point LogicalPoint(Point point) => new(point.X / _scale, point.Y / _scale);

    private void BeginPointer(nint window, Point location)
    {
        var geometry = Geometry;
        var point = LogicalPoint(location);
        var edges = geometry.ResizeAt(point);
        if (edges != NoteResizeEdge.None)
        {
            _resizeEdges = edges;
            StartPointer(window, location, PointerAction.Resize);
        }
        else if (geometry.Mode.Contains(point))
        {
            SetClickThrough(true);
        }
        else if (_maxScroll > 0 && geometry.Track.Contains(point))
        {
            var thumb = geometry.Thumb(_contentHeight, ScrollOffset);
            _thumbOffset = thumb.Contains(point) ? (float)(point.Y - thumb.Y) : (float)thumb.Height / 2;
            StartPointer(window, location, PointerAction.Scroll);
            ScrollTo(geometry.ScrollAt(_contentHeight, (float)point.Y - _thumbOffset));
        }
        else if (geometry.Index.Contains(point))
        {
            ToggleIndex();
        }
        else if (geometry.Previous.Contains(point))
        {
            if (_indexOpen || _steps.Count == 1) ScrollTo(0);
            else Go(-1);
        }
        else if (geometry.Next.Contains(point))
        {
            if (_indexOpen || _steps.Count == 1) ScrollTo(_maxScroll);
            else Go(1);
        }
        else if (_indexOpen && geometry.Body.Contains(point))
        {
            var index = (int)((point.Y - geometry.Body.Y + _indexScroll) / NoteOverlayGeometry.IndexRowHeight);
            if (index < _steps.Count) SelectStep(index);
        }
        else if (point.Y < NoteOverlayGeometry.HeaderHeight)
        {
            StartPointer(window, location, PointerAction.Move);
        }
    }

    private void StartPointer(nint window, Point location, PointerAction action)
    {
        _pointerAction = action;
        _pointerStart = new Point { X = _bounds.Left + location.X, Y = _bounds.Top + location.Y };
        _dragBounds = _bounds;
        SetCapture(window);
        // A non-activating window may lose mouse messages outside its bounds.
        // Track the held button during an explicit drag so resizing works there too.
        _trackingPointer = SetTimer(window, PointerTimer, 16, 0) != 0;
    }

    private void DragPointer(Point location)
    {
        if (_pointerAction == PointerAction.Scroll)
        {
            ScrollTo(Geometry.ScrollAt(_contentHeight, (float)LogicalPoint(location).Y - _thumbOffset));
            return;
        }
        if (_placement is null || !KeepVisibleIfProtected()) return;

        var dx = _bounds.Left + location.X - _pointerStart.X;
        var dy = _bounds.Top + location.Y - _pointerStart.Y;
        if (_pointerAction == PointerAction.Move)
        {
            var next = NoteOverlayPlacement.Clamp(_dragBounds with
            {
                Left = _dragBounds.Left + dx, Top = _dragBounds.Top + dy
            }, _placement.WorkArea);
            if (next != _bounds && SetWindowPos(_window, -1, next.Left, next.Top, 0, 0, NoActivate | 1))
                _bounds = next;
            return;
        }

        var resized = NoteOverlayPlacement.Resize(_dragBounds, _placement.WorkArea, _resizeEdges, dx, dy,
            (int)Math.Ceiling(NoteOverlayGeometry.MinimumWidth * _scale), (int)Math.Ceiling(NoteOverlayGeometry.MinimumHeight * _scale));
        if (resized == _bounds) return;
        if (!SetWindowPos(_window, -1, resized.Left, resized.Top, resized.Width, resized.Height, NoActivate))
            return;

        _bounds = resized;
        InvalidateRect(_window, 0, false);
        if (!_resizeRenderPending)
        {
            _resizeRenderPending = SetTimer(_window, ResizeTimer, 32, 0) != 0;
            if (!_resizeRenderPending) Refresh();
        }
    }

    private void EndPointer(bool release)
    {
        var action = _pointerAction;
        _pointerAction = PointerAction.None;
        if (_trackingPointer)
        {
            KillTimer(_window, PointerTimer);
            _trackingPointer = false;
        }
        if (release && action != PointerAction.None) ReleaseCapture();

        if (action != PointerAction.Resize) return;
        CancelResizeRender();
        if (_dragBounds.Width == _bounds.Width && _dragBounds.Height == _bounds.Height) return;
        _width = _bounds.Width / _scale;
        _height = _bounds.Height / _scale;
        if (_shown) Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TrackPointer()
    {
        if (!_trackingPointer) return;
        if (GetAsyncKeyState(1) >= 0) EndPointer(release: true);
        else if (GetCursorPos(out var cursor))
            DragPointer(new() { X = cursor.X - _bounds.Left, Y = cursor.Y - _bounds.Top });
    }

    private void CancelResizeRender()
    {
        if (!_resizeRenderPending) return;
        KillTimer(_window, ResizeTimer);
        _resizeRenderPending = false;
    }

    private void UpdatePointerCursor()
    {
        if (!GetCursorPos(out var cursor)) return;
        var position = LogicalPoint(new() { X = cursor.X - _bounds.Left, Y = cursor.Y - _bounds.Top });
        var edges = _pointerAction == PointerAction.Resize ? _resizeEdges : Geometry.ResizeAt(position);
        var cursorId = edges switch
        {
            NoteResizeEdge.Left or NoteResizeEdge.Right => 32644,
            NoteResizeEdge.Top or NoteResizeEdge.Bottom => 32645,
            (NoteResizeEdge.Left | NoteResizeEdge.Top) or (NoteResizeEdge.Right | NoteResizeEdge.Bottom) => 32642,
            (NoteResizeEdge.Right | NoteResizeEdge.Top) or (NoteResizeEdge.Left | NoteResizeEdge.Bottom) => 32643,
            _ => position.Y < NoteOverlayGeometry.HeaderHeight && !Geometry.Mode.Contains(position) ? 32646 : 32512
        };
        SetCursor(LoadCursor(0, cursorId));
    }
}
