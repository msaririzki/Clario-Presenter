using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace Clario_Presenter.Notes;

internal static class NoteOverlayRenderer
{
    internal const float Header = 66;
    internal const float Footer = 34;
    private const float Margin = 16;
    private const string BodyFont = "Segoe UI";
    private const string CodeFont = "Consolas";
    private static readonly Color White = Color.FromArgb(255, 242, 245, 250);
    private static readonly Color Muted = Color.FromArgb(255, 179, 190, 209);

    internal static float Draw(CanvasDevice device, CanvasDrawingSession session, NoteStep step,
        int index, int count, float width, float height, float textSize, bool clickThrough, float scroll)
    {
        session.Clear(Color.FromArgb(255, 17, 22, 31));
        session.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        session.DrawRoundedRectangle(1, 1, width - 2, height - 2, 10, 10, Color.FromArgb(180, 99, 115, 144));
        var bodyHeight = Math.Max(1, height - Header - Footer);
        var layouts = new List<(CanvasTextLayout Layout, bool IsCode, float Height)>();
        try
        {
            foreach (var block in step.Blocks)
            {
                var code = block.Kind == NoteBlockKind.Code;
                var content = string.Concat(block.Runs.Select(run => run.Text));
                if (content.Length == 0) continue;
                using var format = new CanvasTextFormat
                {
                    FontFamily = code ? CodeFont : BodyFont,
                    FontSize = block.Kind == NoteBlockKind.Heading ? textSize + 1 : textSize,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = block.Kind == NoteBlockKind.Heading ? (ushort)600 : (ushort)400 },
                    WordWrapping = CanvasWordWrapping.Wrap
                };
                var layout = new CanvasTextLayout(device, content, format, width - Margin * 2 - (code ? 20 : 0), 0);
                layouts.Add((layout, code, 0));
                var offset = 0;
                foreach (var run in block.Runs)
                {
                    if (run.Text.Length > 0)
                    {
                        if (run.Bold) layout.SetFontWeight(offset, run.Text.Length, new Windows.UI.Text.FontWeight { Weight = 700 });
                        if (!code && run.Monospace) layout.SetFontFamily(offset, run.Text.Length, CodeFont);
                    }
                    offset += run.Text.Length;
                }
                layouts[^1] = (layout, code, (float)Math.Ceiling(layout.LayoutBounds.Height) + (code ? 18 : 0));
            }

            var maxScroll = Math.Max(0, layouts.Sum(item => item.Height + 9) - bodyHeight);
            var y = Header - Math.Clamp(scroll, 0, maxScroll);
            using (session.CreateLayer(1, new Rect(Margin, Header, width - Margin * 2, bodyHeight)))
            {
                foreach (var (layout, code, blockHeight) in layouts)
                {
                    if (y + blockHeight >= Header && y < height - Footer)
                    {
                        if (code)
                            session.FillRoundedRectangle(Margin, y, width - Margin * 2, blockHeight, 5, 5,
                                Color.FromArgb(255, 30, 39, 56));
                        session.DrawTextLayout(layout, Margin + (code ? 10 : 0), y + (code ? 9 : 0), White);
                    }
                    y += blockHeight + 9;
                }
            }

            DrawLine(session, $"CATATAN  ·  {index + 1}/{count}", Margin, 11, width - 130, 11, Muted);
            DrawLine(session, clickThrough ? "Klik tembus" : "Atur · geser", width - 102, 11, 88, 11, Muted);
            DrawLine(session, step.Title, Margin, 29, width - Margin * 2, 19, White);
            session.DrawLine(Margin, height - Footer, width - Margin, height - Footer, Color.FromArgb(140, 99, 115, 144));
            DrawLine(session, "‹  Sebelumnya", Margin, height - 26, 108, 12, index > 0 ? White : Muted);
            DrawLine(session, "Berikutnya  ›", width - 100, height - 26, 90, 12, index + 1 < count ? White : Muted);
            return maxScroll;
        }
        finally
        {
            foreach (var (layout, _, _) in layouts) layout.Dispose();
        }
    }

    private static void DrawLine(CanvasDrawingSession session, string text, float x, float y, float width, float size, Color color)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = BodyFont,
            FontSize = size,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis
        };
        session.DrawText(text, new Rect(x, y, width, size + 6), color, format);
    }
}
