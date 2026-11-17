using Clario_Presenter.Services;

namespace Clario_Presenter.Notes;

[Flags]
internal enum NoteResizeEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

internal static class NoteOverlayPlacement
{
    internal static bool IsAllowed(DisplayBounds panel, bool excludedFromCapture,
        DisplayBounds? recordedDisplay, DisplayBounds? audienceDisplay) =>
        (audienceDisplay is null || !Overlaps(panel, audienceDisplay.Value))
        && (excludedFromCapture || recordedDisplay is null || !Overlaps(panel, recordedDisplay.Value));

    internal static bool Overlaps(DisplayBounds a, DisplayBounds b) =>
        a.Width > 0 && a.Height > 0 && b.Width > 0 && b.Height > 0
        && a.Left < b.Left + b.Width && b.Left < a.Left + a.Width
        && a.Top < b.Top + b.Height && b.Top < a.Top + a.Height;

    internal static DisplayBounds Clamp(DisplayBounds requested, DisplayBounds work)
    {
        var width = Math.Clamp(requested.Width, 1, Math.Max(1, work.Width));
        var height = Math.Clamp(requested.Height, 1, Math.Max(1, work.Height));
        return new(Math.Clamp(requested.Left, work.Left, work.Left + Math.Max(0, work.Width - width)),
            Math.Clamp(requested.Top, work.Top, work.Top + Math.Max(0, work.Height - height)), width, height);
    }

    internal static DisplayBounds Resize(DisplayBounds start, DisplayBounds work, NoteResizeEdge edges,
        int dx, int dy, int minimumWidth, int minimumHeight)
    {
        var left = start.Left;
        var top = start.Top;
        var right = left + start.Width;
        var bottom = top + start.Height;
        var minWidth = Math.Min(start.Width, minimumWidth);
        var minHeight = Math.Min(start.Height, minimumHeight);
        if ((edges & NoteResizeEdge.Left) != 0) left = Math.Clamp(left + dx, work.Left, right - minWidth);
        if ((edges & NoteResizeEdge.Right) != 0) right = Math.Clamp(right + dx, left + minWidth, work.Left + work.Width);
        if ((edges & NoteResizeEdge.Top) != 0) top = Math.Clamp(top + dy, work.Top, bottom - minHeight);
        if ((edges & NoteResizeEdge.Bottom) != 0) bottom = Math.Clamp(bottom + dy, top + minHeight, work.Top + work.Height);
        return new(left, top, right - left, bottom - top);
    }
}
