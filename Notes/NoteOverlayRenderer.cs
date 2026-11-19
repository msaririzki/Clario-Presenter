using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace Clario_Presenter.Notes;

internal sealed class NoteOverlayRenderer : IDisposable
{
    private const string BodyFont = "Segoe UI";
    private static readonly Color White = Color.FromArgb(255, 242, 245, 250);
    private static readonly Color Muted = Color.FromArgb(255, 179, 190, 209);
    private static readonly Color Outline = Color.FromArgb(255, 99, 115, 144);
    private static readonly Color Surface = Color.FromArgb(255, 30, 39, 56);
    private static readonly Color Selection = Color.FromArgb(255, 47, 54, 88);
    private readonly List<NoteBlockLayout> _blocks = [];
    private CanvasDevice? _device;
    private IReadOnlyList<NoteStep>? _document;
    private int[] _firstBlock = [];
    private float[] _sectionTops = [];
    private float _width;
    private float _textSize;
    private float _contentHeight;

    internal float Measure(CanvasDevice device, IReadOnlyList<NoteStep> document, float width, float textSize, ref float scroll)
    {
        var sameDocument = ReferenceEquals(_document, document);
        var rebuild = !sameDocument || !ReferenceEquals(_device, device) || textSize != _textSize;
        if (!rebuild && width == _width) return _contentHeight;

        // Remember the text at the top, even when a single paragraph wraps across many screens.
        var anchor = sameDocument && scroll > 0 ? FirstVisibleBlock(scroll) : -1;
        var character = 0;
        var lineOffset = 0f;
        if (anchor >= 0 && anchor < _blocks.Count)
        {
            var block = _blocks[anchor];
            (character, lineOffset) = block.Anchor(scroll - block.Top);
        }

        if (rebuild)
        {
            Dispose();
            _firstBlock = new int[document.Count];
            _sectionTops = new float[document.Count];
            for (var i = 0; i < document.Count; i++)
            {
                var section = document[i];
                _firstBlock[i] = _blocks.Count;
                if (section.DocumentTitle is { } title)
                    _blocks.Add(new(new(NoteBlockKind.Heading, [new(title)], Level: 1), textSize));
                if (document.Count > 1 || section.Title != "Catatan")
                    _blocks.Add(new(new(NoteBlockKind.Heading, [new(section.Title)], Level: 2), textSize));
                foreach (var block in section.Blocks) _blocks.Add(new(block, textSize));
            }
        }

        _contentHeight = 0;
        foreach (var block in _blocks)
        {
            block.Measure(device, width);
            block.Top = _contentHeight;
            _contentHeight += block.Height + block.Gap;
        }
        if (_blocks.Count > 0) _contentHeight -= _blocks[^1].Gap;
        for (var i = 0; i < _sectionTops.Length; i++)
            _sectionTops[i] = _firstBlock[i] < _blocks.Count ? _blocks[_firstBlock[i]].Top : _contentHeight;

        if (anchor >= 0 && anchor < _blocks.Count)
        {
            var block = _blocks[anchor];
            scroll = block.Top + block.AnchorY(character, lineOffset);
        }

        _device = device;
        _document = document;
        _width = width;
        _textSize = textSize;
        return _contentHeight;
    }

    internal float SectionTop(int section) => _sectionTops[section];

    internal int SectionAt(float scroll)
    {
        var found = Array.BinarySearch(_sectionTops, scroll);
        return found >= 0 ? found : Math.Max(0, ~found - 1);
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

        DrawLine(session, indexOpen ? "Daftar bagian" : "Catatan", new Rect(14, 4, geometry.Width / 2, 27), 15, White);
        if (steps.Count > 1)
            DrawLine(session, $"Bagian {index + 1}/{steps.Count}", new Rect(geometry.Width / 2, 4, geometry.Width / 2 - 14, 27),
                12, Muted, alignment: CanvasHorizontalAlignment.Right);
        session.DrawLine(10, geometry.Height - NoteOverlayGeometry.FooterHeight,
            geometry.Width - 10, geometry.Height - NoteOverlayGeometry.FooterHeight, Outline);
        DrawButton(session, "↑ Naik", geometry.Previous, scroll > 0);
        DrawButton(session, "↓ Turun", geometry.Next, scroll < maxScroll);
        if (steps.Count > 1)
            DrawButton(session, indexOpen ? "Kembali" : "Daftar", geometry.Index, true);
        DrawButton(session, clickThrough ? "Atur" : "Selesai", geometry.Mode, true);

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

    internal void DrawBody(CanvasDrawingSession session, Rect body, float scroll)
    {
        // Blocks below the viewport need no drawing. Reuse the measured layouts while scrolling.
        var bottom = scroll + body.Height;
        for (var index = FirstVisibleBlock(scroll); index < _blocks.Count && _blocks[index].Top < bottom; index++)
        {
            var block = _blocks[index];
            var y = (float)body.Y + block.Top - scroll;
            block.Draw(session, (float)body.X, y, White, Muted, Outline, Surface);
        }
    }

    private int FirstVisibleBlock(float scroll)
    {
        var low = 0;
        var high = _blocks.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            var block = _blocks[middle];
            if (block.Top + block.Height <= scroll) low = middle + 1;
            else high = middle;
        }

        return low;
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
        DrawLine(session, label, rect, 12, enabled ? White : Outline, alignment: CanvasHorizontalAlignment.Center);
    }

    private static void DrawLine(CanvasDrawingSession session, string text, Rect rect, float size, Color color, CanvasHorizontalAlignment alignment = CanvasHorizontalAlignment.Left)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = BodyFont,
            FontSize = size,
            HorizontalAlignment = alignment,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis
        };
        session.DrawText(text, rect, color, format);
    }

    public void Dispose()
    {
        foreach (var block in _blocks) block.Dispose();
        _blocks.Clear();
        _document = null;
        _device = null;
        _contentHeight = 0;
    }

}
