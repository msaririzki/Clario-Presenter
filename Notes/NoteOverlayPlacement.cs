using Clario_Presenter.Services;

namespace Clario_Presenter.Notes;

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
}
