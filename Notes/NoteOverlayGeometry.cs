using Windows.Foundation;

namespace Clario_Presenter.Notes;

internal readonly record struct NoteOverlayGeometry(float Width, float Height)
{
    internal const float HeaderHeight = 36;
    internal const float FooterHeight = 42;
    internal const float IndexRowHeight = 36;
    internal const int MinimumWidth = 264;
    internal const int MinimumHeight = 168;

    internal Rect Body => new(14, HeaderHeight, Math.Max(1, Width - 42), Math.Max(1, Height - HeaderHeight - FooterHeight));
    internal Rect Track => new(Width - 25, HeaderHeight, 14, Body.Height);
    private float ButtonWidth => (Width - 32) / 4;
    private Rect FooterButton(int column) => new(10 + column * (ButtonWidth + 4), Height - 35, ButtonWidth, 27);
    internal Rect Previous => FooterButton(0);
    internal Rect Next => FooterButton(1);
    internal Rect Index => FooterButton(2);
    internal Rect Mode => FooterButton(3);

    internal float ScrollLimit(float contentHeight) => Math.Max(0, contentHeight - (float)Body.Height);

    internal Rect Thumb(float contentHeight, float scroll)
    {
        var trackHeight = (float)Track.Height;
        var height = Math.Clamp(trackHeight * trackHeight / Math.Max(1, contentHeight), Math.Min(22, trackHeight), trackHeight);
        var progress = Math.Clamp(scroll / Math.Max(1, ScrollLimit(contentHeight)), 0, 1);
        return new(Track.X, Track.Y + (trackHeight - height) * progress, Track.Width, height);
    }

    internal float ScrollAt(float contentHeight, float thumbTop)
    {
        var travel = (float)(Track.Height - Thumb(contentHeight, 0).Height);
        return travel > 0 ? Math.Clamp((thumbTop - (float)Track.Y) / travel, 0, 1) * ScrollLimit(contentHeight) : 0;
    }

    internal NoteResizeEdge ResizeAt(Point point)
    {
        const int edge = 7;
        const int corner = 15;
        var horizontal = point.X < edge ? NoteResizeEdge.Left : point.X >= Width - edge ? NoteResizeEdge.Right : 0;
        if (point.Y < edge) return horizontal | NoteResizeEdge.Top;
        if (point.Y >= Height - corner)
        {
            if (point.X < corner) return NoteResizeEdge.Left | NoteResizeEdge.Bottom;
            if (point.X >= Width - corner) return NoteResizeEdge.Right | NoteResizeEdge.Bottom;
            if (point.Y >= Height - edge) return NoteResizeEdge.Bottom;
        }
        return horizontal;
    }
}
