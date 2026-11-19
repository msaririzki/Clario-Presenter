using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Clario_Presenter.Notes;

internal enum NoteBlockKind { Text, Heading, Code, Divider, TableRow }
internal readonly record struct NoteRun(string Text, bool Bold = false, bool Monospace = false,
    bool Italic = false, bool Strike = false);
internal sealed record NoteTableRow(IReadOnlyList<NoteRun>[] Cells, IReadOnlyList<NoteRun>[] Headers,
    TableColumnAlign?[] Alignments, float[] ColumnEdges, bool IsHeader, bool HasData);
internal sealed record NoteBlock(NoteBlockKind Kind, IReadOnlyList<NoteRun> Runs, int Level = 0,
    int Indent = 0, string Marker = "", bool Quoted = false, NoteTableRow? Row = null);
internal sealed record NoteStep(string Title, IReadOnlyList<NoteBlock> Blocks, string? DocumentTitle = null);

internal static class NoteDocument
{
    public const int MaximumLength = 200_000;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseTaskLists().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough).DisableHtml().Build();

    public static IReadOnlyList<NoteStep> Parse(string text, bool markdown)
    {
        text = text.ReplaceLineEndings("\n");
        if (!markdown) return [new("Catatan", [new(NoteBlockKind.Text, [new(text)])])];

        var steps = new List<NoteStep>();
        var title = "Catatan";
        string? documentTitle = null;
        var blocks = new List<NoteBlock>();
        var explicitStep = false;
        foreach (var block in Markdown.Parse(text, Pipeline))
        {
            if (block is HeadingBlock { Level: 2 } heading)
            {
                if (blocks.Count > 0 || explicitStep) steps.Add(new(title, blocks));
                else if (title != "Catatan") documentTitle = title;
                title = HeadingText(heading);
                if (title.Length == 0) title = "Langkah";
                blocks = [];
                explicitStep = true;
            }
            else if (block is HeadingBlock { Level: 1 } documentHeading && !explicitStep && blocks.Count == 0 && title == "Catatan")
                title = HeadingText(documentHeading);
            else
                Append(block, blocks);
        }
        if (blocks.Count > 0 || explicitStep || steps.Count == 0)
            steps.Add(new(title.Length == 0 ? "Catatan" : title, blocks));
        if (documentTitle is not null) steps[0] = steps[0] with { DocumentTitle = documentTitle };
        return steps;
    }

    private static string HeadingText(HeadingBlock heading) =>
        string.Concat(ReadRuns(heading.Inline).Select(run => run.Text)).Trim();

    private static void Append(Block block, List<NoteBlock> blocks, int indent = 0, string marker = "", bool quoted = false)
    {
        switch (block)
        {
            case CodeBlock code:
                blocks.Add(new(NoteBlockKind.Code, [new(code.Lines.ToString().TrimEnd('\r', '\n'), Monospace: true)],
                    Indent: indent, Marker: marker, Quoted: quoted));
                break;
            case ListBlock list:
                var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var bullet = list.IsOrdered ? $"{number++}. " : "• ";
                    foreach (var child in item)
                    {
                        Append(child, blocks, indent + 1, bullet, quoted);
                        bullet = "";
                    }
                }
                break;
            case QuoteBlock quote:
                foreach (var child in quote)
                {
                    Append(child, blocks, indent, marker, true);
                    marker = "";
                }
                break;
            case Table table:
                var rows = table.OfType<TableRow>().ToArray();
                var align = table.ColumnDefinitions.Select(column => column.Alignment).ToArray();
                IReadOnlyList<NoteRun>[] headers = [];
                var contents = rows.Select(row => row.OfType<TableCell>().Select(cell => (IReadOnlyList<NoteRun>)cell
                    .OfType<LeafBlock>().SelectMany(leaf => ReadRuns(leaf.Inline)).ToList()).ToArray()).ToArray();
                var weights = new float[contents.Select(cells => cells.Length).DefaultIfEmpty().Max()];
                foreach (var cells in contents)
                    for (int i = 0; i < cells.Length; i++)
                        weights[i] = Math.Max(weights[i], Math.Clamp(cells[i].Sum(run => run.Text.Length), 4, 32));
                var total = weights.Sum();
                var edges = new float[weights.Length + 1];
                for (int i = 0; i < weights.Length; i++) edges[i + 1] = edges[i] + weights[i] / total;
                for (int index = 0; index < rows.Length; index++)
                {
                    var row = rows[index];
                    var cells = contents[index];
                    if (row.IsHeader) headers = cells;
                    blocks.Add(new(NoteBlockKind.TableRow, [], Indent: indent, Marker: marker, Quoted: quoted,
                        Row: new(cells, headers, align, edges, row.IsHeader, rows.Length > 1)));
                    marker = "";
                }
                break;
            case ThematicBreakBlock:
                blocks.Add(new(NoteBlockKind.Divider, [], Indent: indent, Quoted: quoted));
                break;
            case LeafBlock leaf when leaf.Inline is not null:
                if (leaf.Inline.FirstChild is TaskList task) marker = task.Checked ? "☑ " : "☐ ";
                blocks.Add(new(block is HeadingBlock ? NoteBlockKind.Heading : NoteBlockKind.Text, ReadRuns(leaf.Inline),
                    (block as HeadingBlock)?.Level ?? 0, indent, marker, quoted));
                break;
        }
    }

    private static List<NoteRun> ReadRuns(ContainerInline? container)
    {
        var runs = new List<NoteRun>();
        if (container is not null) AppendInline(container, runs, new NoteRun(""));
        runs.RemoveAll(run => run.Text.Length == 0);
        return runs;
    }

    private static void AppendInline(Inline node, List<NoteRun> runs, NoteRun style)
    {
        switch (node)
        {
            case LiteralInline literal: runs.Add(style with { Text = literal.Content.ToString() }); break;
            case HtmlEntityInline entity: runs.Add(style with { Text = entity.Transcoded.ToString() }); break;
            case CodeInline code: runs.Add(style with { Text = code.Content, Monospace = true }); break;
            case LineBreakInline line: runs.Add(style with { Text = line.IsHard ? "\n" : " " }); break;
            case AutolinkInline link: runs.Add(style with { Text = link.Url }); break;
            case ContainerInline container:
                if (node is EmphasisInline emphasis)
                {
                    if (emphasis.DelimiterChar == '~') style = style with { Strike = true };
                    else style = style with { Bold = style.Bold || emphasis.DelimiterCount >= 2,
                        Italic = style.Italic || emphasis.DelimiterCount % 2 == 1 };
                }
                // Keep link labels and image descriptions local to the overlay.
                foreach (var child in container) AppendInline(child, runs, style);
                break;
        }
    }
}
