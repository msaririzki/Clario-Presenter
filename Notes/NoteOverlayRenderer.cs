using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace Clario_Presenter.Notes;

internal sealed class NoteOverlayRenderer : IDisposable
{
    private const string BodyFont = "Segoe UI";
    private const string CodeFont = "Consolas";
    private const float BlockGap = 9;
    private static readonly Color White = Color.FromArgb(255, 242, 245, 250);
    private static readonly Color Muted = Color.FromArgb(255, 179, 190, 209);
    private static readonly Color Outline = Color.FromArgb(255, 99, 115, 144);
    private static readonly Color Surface = Color.FromArgb(255, 30, 39, 56);
    private static readonly Color Selection = Color.FromArgb(255, 47, 54, 88);
    private readonly List<BlockLayout> _blocks = [];
    private CanvasDevice? _device;
    private NoteStep? _step;
    private float _width;
    private float _textSize;
    private float _contentHeight;

    internal float Measure(CanvasDevice device, NoteStep step, float width, float textSize)
    {
        if (ReferenceEquals(_device, device) && ReferenceEquals(_step, step) && width == _width && textSize == _textSize)
            return _contentHeight;

        Dispose();
        foreach (var block in step.Blocks)
        {
            var content = string.Concat(block.Runs.Select(run => run.Text));
            if (content.Length == 0) continue;

            var code = block.Kind == NoteBlockKind.Code;
            using var format = new CanvasTextFormat
            {
                FontFamily = code ? CodeFont : BodyFont,
                FontSize = block.Kind == NoteBlockKind.Heading ? textSize + 1 : textSize,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = block.Kind == NoteBlockKind.Heading ? (ushort)600 : (ushort)400 },
                WordWrapping = CanvasWordWrapping.Wrap
            };
            var layout = new CanvasTextLayout(device, content, format, Math.Max(1, width - (code ? 20 : 0)), 0);
            var top = _contentHeight;
            _blocks.Add(new(layout, code, top, 0));
            var offset = 0;
            foreach (var run in block.Runs)
            {
                if (run.Text.Length == 0) continue;
                if (run.Bold) layout.SetFontWeight(offset, run.Text.Length, new Windows.UI.Text.FontWeight { Weight = 700 });
                if (!code && run.Monospace) layout.SetFontFamily(offset, run.Text.Length, CodeFont);
                offset += run.Text.Length;
            }
            var height = (float)Math.Ceiling(layout.LayoutBoundsIncludingTrailingWhitespace.Height) + (code ? 18 : 0);
            _blocks[^1] = new(layout, code, top, height);
            _contentHeight = top + height + BlockGap;
        }

        _device = device;
        _step = step;
        _width = width;
        _textSize = textSize;
        return _contentHeight;
    }

    internal void Draw(CanvasDrawingSession session, NoteOverlayGeometry geometry, IReadOnlyList<NoteStep> steps,
        int index, bool indexOpen, bool clickThrough, float scroll, float contentHeight)
    {
        var body = geometry.Body;
        session.Clear(Color.FromArgb(255, 17, 22, 31));
        session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        session.DrawRoundedRectangle(1, 1, Math.Max(1, geometry.Width - 2), Math.Max(1, geometry.Height - 2), 10, 10, Outline);

        using (session.CreateLayer(1, body))
        {
            if (indexOpen) DrawIndex(session, body, steps, index, scroll);
            else DrawBody(session, body, scroll);
        }

        var maxScroll = geometry.ScrollLimit(contentHeight);
        if (maxScroll > 0)
        {
            var track = geometry.Track;
            var thumb = geometry.Thumb(contentHeight, scroll);
            session.FillRoundedRectangle((float)track.X + 4, (float)track.Y, 6, (float)track.Height, 3, 3, Surface);
            session.FillRoundedRectangle((float)thumb.X + 3, (float)thumb.Y, 8, (float)thumb.Height, 4, 4, Muted);
        }

        DrawLine(session, $"BAGIAN {index + 1}/{steps.Count}", new Rect(14, 8, Math.Max(1, geometry.Width - 127), 22), 11, Muted);
        DrawButton(session, clickThrough ? "Klik tembus" : "Aktifkan tembus", geometry.Mode, true);
        DrawLine(session, indexOpen ? "Daftar bagian" : steps[index].Title,
            new Rect(14, 31, Math.Max(1, geometry.Width - 28), 28), 19, White);
        session.DrawLine(14, geometry.Height - NoteOverlayGeometry.FooterHeight,
            geometry.Width - 14, geometry.Height - NoteOverlayGeometry.FooterHeight, Outline);

        var sectionNavigation = steps.Count > 1 && !indexOpen;
        DrawButton(session, sectionNavigation ? "‹ Sebelum" : "Awal", geometry.Previous,
            sectionNavigation ? index > 0 : scroll > 0);
        DrawButton(session, sectionNavigation ? "Lanjut ›" : "Akhir", geometry.Next,
            sectionNavigation ? index + 1 < steps.Count : scroll < maxScroll);
        if (steps.Count > 1)
            DrawButton(session, indexOpen ? "Kembali" : "Daftar", geometry.Index, true);
        else if (maxScroll > 0)
            DrawLine(session, $"{Math.Round(scroll * 100 / maxScroll)}%", geometry.Index, 12, Muted, centered: true);

        if (!clickThrough)
        {
            var bottom = geometry.Height - 4;
            for (var leg = 4; leg <= 12; leg += 4)
            {
                session.DrawLine(4, bottom - leg, 4 + leg, bottom, Outline);
                session.DrawLine(geometry.Width - 4, bottom - leg, geometry.Width - 4 - leg, bottom, Outline);
            }
        }
    }

    private void DrawBody(CanvasDrawingSession session, Rect body, float scroll)
    {
        // Blocks below the viewport need no drawing. Reuse the measured layouts while scrolling.
        var bottom = scroll + body.Height;
        var low = 0;
        var high = _blocks.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            var block = _blocks[middle];
            if (block.Top + block.Height <= scroll) low = middle + 1;
            else high = middle;
        }

        for (var index = low; index < _blocks.Count && _blocks[index].Top < bottom; index++)
        {
            var block = _blocks[index];
            var y = (float)body.Y + block.Top - scroll;
            if (block.IsCode)
                session.FillRoundedRectangle((float)body.X, y, (float)body.Width, block.Height, 5, 5, Surface);
            session.DrawTextLayout(block.Layout, (float)body.X + (block.IsCode ? 10 : 0), y + (block.IsCode ? 9 : 0), White);
        }
    }

    private static void DrawIndex(CanvasDrawingSession session, Rect body, IReadOnlyList<NoteStep> steps,
        int currentStep, float scroll)
    {
        var rowHeight = NoteOverlayGeometry.IndexRowHeight;
        var first = (int)(scroll / rowHeight);
        for (var index = first; index < steps.Count; index++)
        {
            var y = (float)body.Y + index * rowHeight - scroll;
            if (y >= body.Bottom) break;
            var selected = index == currentStep;
            session.FillRoundedRectangle((float)body.X, y + 2, (float)body.Width, rowHeight - 4, 5, 5, selected ? Selection : Surface);
            DrawLine(session, $"{index + 1}.  {steps[index].Title}", new Rect(body.X + 9, y, Math.Max(1, body.Width - 18), rowHeight),
                13, selected ? White : Muted);
        }
    }

    private static void DrawButton(CanvasDrawingSession session, string label, Rect rect, bool enabled)
    {
        session.FillRoundedRectangle(rect, 5, 5, Surface);
        DrawLine(session, label, rect, 12, enabled ? White : Outline, centered: true);
    }

    private static void DrawLine(CanvasDrawingSession session, string text, Rect rect, float size, Color color, bool centered = false)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = BodyFont,
            FontSize = size,
            HorizontalAlignment = centered ? CanvasHorizontalAlignment.Center : CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis
        };
        session.DrawText(text, rect, color, format);
    }

    public void Dispose()
    {
        foreach (var block in _blocks) block.Layout.Dispose();
        _blocks.Clear();
        _step = null;
        _device = null;
        _contentHeight = 0;
    }

    private readonly record struct BlockLayout(CanvasTextLayout Layout, bool IsCode, float Top, float Height);
}
