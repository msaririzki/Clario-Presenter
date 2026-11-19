using Markdig.Extensions.Tables;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace Clario_Presenter.Notes;

// The same measurements drive drawing, scrolling and resize anchors.
internal sealed class NoteBlockLayout(NoteBlock block, float fontSize) : IDisposable
{
    private readonly List<CanvasTextLayout> _text = [];
    private CanvasTextLayout? _marker;
    private bool _stacked;
    private float _left, _inset, _available;
    internal float Top { get; set; }
    internal float Height { get; private set; }
    internal float Gap => Height == 0 ? 0 : block.Row is not null ? 2 : 9;
    private bool Code => block.Kind == NoteBlockKind.Code;
    private float TextTop => Code || block.Row is not null ? 8 : 0;

    internal void Measure(CanvasDevice device, float width)
    {
        _left = Math.Min(block.Indent * fontSize * 0.95f, width / 4) + (block.Quoted ? 12 : 0);
        if (block.Marker.Length > 0 && _marker is null)
            _marker = Layout(device, [new(block.Marker)], false, CanvasHorizontalAlignment.Left);
        if (_marker is not null) _left = Math.Max(_left, (float)_marker.LayoutBounds.Width + 8);
        _available = Math.Max(1, width - _left);
        if (block.Kind == NoteBlockKind.Divider) { Height = 12; return; }
        var row = block.Row;
        var columns = Math.Max(1, (row?.ColumnEdges.Length ?? 1) - 1);
        var stacked = row is not null && _available / columns < fontSize * 6;
        if (stacked != _stacked)
        {
            ClearText();
            _stacked = stacked;
        }
        if (stacked && row is { IsHeader: true, HasData: true }) { Height = 0; return; }

        _inset = Code || row is not null ? 9 : 0;
        if (_text.Count == 0)
        {
            if (row is null) _text.Add(Layout(device, block.Runs, false, CanvasHorizontalAlignment.Left));
            else if (stacked) _text.Add(Layout(device, StackedFields(row), false, CanvasHorizontalAlignment.Left));
            else
            {
                for (int i = 0; i < row.Cells.Length; i++)
                {
                    var alignment = row.Alignments.ElementAtOrDefault(i) switch
                    {
                        TableColumnAlign.Center => CanvasHorizontalAlignment.Center,
                        TableColumnAlign.Right => CanvasHorizontalAlignment.Right,
                        _ => CanvasHorizontalAlignment.Left
                    };
                    _text.Add(Layout(device, row.Cells[i], row.IsHeader, alignment));
                }
            }
        }
        float textHeight = 0;
        for (int i = 0; i < _text.Count; i++)
        {
            var layout = _text[i];
            layout.RequestedSize = new Size(Math.Max(1, ColumnEdge(i + 1) - ColumnEdge(i) - 2 * _inset), 0);
            textHeight = Math.Max(textHeight, (float)Math.Ceiling(layout.LayoutBoundsIncludingTrailingWhitespace.Height));
        }
        Height = Math.Max(fontSize, textHeight) + 2 * TextTop;
    }

    private float ColumnEdge(int index)
    {
        if (_stacked || block.Row is not { } row) return index == 0 ? 0 : _available;
        // Reserve reading space for short labels, then give longer values the surplus.
        var minimum = fontSize * 3.5f;
        var surplus = _available - minimum * (row.ColumnEdges.Length - 1);
        return index * minimum + row.ColumnEdges[index] * surplus;
    }

    private static List<NoteRun> StackedFields(NoteTableRow row)
    {
        var result = new List<NoteRun>();
        for (int i = 0; i < row.Cells.Length; i++)
        {
            if (i > 0) result.Add(new("\n"));
            if (!row.IsHeader && i < row.Headers.Length && row.Headers[i].Any(run => run.Text.Length > 0))
            {
                result.AddRange(row.Headers[i].Select(run => run with { Bold = true }));
                result.Add(new("  ·  "));
            }
            result.AddRange(row.Cells[i]);
        }
        return result;
    }

    private CanvasTextLayout Layout(CanvasDevice device, IReadOnlyList<NoteRun> runs, bool header, CanvasHorizontalAlignment align)
    {
        var heading = block.Kind == NoteBlockKind.Heading;
        using var format = new CanvasTextFormat
        {
            FontFamily = Code ? "Consolas" : "Segoe UI",
            FontSize = fontSize + (heading ? block.Level switch { 1 => 5, 2 => 3, _ => 1 } : 0),
            FontWeight = new FontWeight { Weight = heading || header ? (ushort)600 : (ushort)400 },
            HorizontalAlignment = align, WordWrapping = CanvasWordWrapping.Wrap
        };
        var layout = new CanvasTextLayout(device, string.Concat(runs.Select(run => run.Text)), format, 16384, 0);
        var offset = 0;
        foreach (var run in runs)
        {
            var count = run.Text.Length;
            if (count == 0) continue;
            if (run.Bold) layout.SetFontWeight(offset, count, new FontWeight { Weight = 700 });
            if (run.Italic) layout.SetFontStyle(offset, count, FontStyle.Italic);
            if (run.Strike) layout.SetStrikethrough(offset, count, true);
            if (!Code && run.Monospace)
            {
                layout.SetFontFamily(offset, count, "Consolas");
                layout.SetColor(offset, count, Color.FromArgb(255, 167, 216, 241));
            }
            offset += count;
        }
        return layout;
    }

    internal (int Character, float Offset) Anchor(float y)
    {
        if (_text.Count != 1 || block.Row is not null) return (-1, y);
        _text[0].HitTest(0, Math.Max(0, y - TextTop), out var region);
        return (region.CharacterIndex, y - TextTop - (float)region.LayoutBounds.Y);
    }

    internal float AnchorY(int character, float offset)
    {
        if (character < 0 || _text.Count != 1) return Math.Min(offset, Height);
        _text[0].GetCaretPosition(character, false, out var region);
        return TextTop + (float)region.LayoutBounds.Y + offset;
    }

    internal void Draw(CanvasDrawingSession session, float left, float y, Color text, Color muted, Color outline, Color surface)
    {
        if (Height == 0) return;
        var x = left + _left;
        if (block.Quoted) session.FillRoundedRectangle(left + 1, y, 3, Height, 1.5f, 1.5f, outline);
        if (_marker is not null)
            session.DrawTextLayout(_marker, x - (float)_marker.LayoutBounds.Width - 5, y + TextTop, muted);
        if (block.Kind == NoteBlockKind.Divider)
        {
            session.DrawLine(x, y + 6, x + _available, y + 6, outline);
            return;
        }
        if (Code || block.Row?.IsHeader == true || _stacked)
            session.FillRoundedRectangle(x, y, _available, Height, 4, 4, surface);
        if (block.Row is not null && !_stacked)
        {
            session.DrawLine(x, y + Height, x + _available, y + Height, outline);
            for (int i = 1; i < _text.Count; i++)
                session.DrawLine(x + ColumnEdge(i), y + 3, x + ColumnEdge(i), y + Height - 3, outline);
        }
        for (int i = 0; i < _text.Count; i++)
            session.DrawTextLayout(_text[i], x + ColumnEdge(i) + _inset, y + TextTop, text);
    }

    private void ClearText()
    {
        foreach (var layout in _text) layout.Dispose();
        _text.Clear();
    }

    public void Dispose()
    {
        ClearText();
        _marker?.Dispose();
    }
}
