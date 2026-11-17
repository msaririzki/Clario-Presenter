using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Clario_Presenter.Notes;

internal enum NoteBlockKind { Text, Heading, Code }
internal readonly record struct NoteRun(string Text, bool Bold = false, bool Monospace = false);
internal sealed record NoteBlock(NoteBlockKind Kind, IReadOnlyList<NoteRun> Runs);
internal sealed record NoteStep(string Title, IReadOnlyList<NoteBlock> Blocks);

internal static class NoteDocument
{
    public const int MaximumLength = 200_000;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public static IReadOnlyList<NoteStep> Parse(string text, bool markdown)
    {
        text = text.ReplaceLineEndings("\n");
        if (!markdown) return [new("Catatan", [new(NoteBlockKind.Text, [new(text)])])];

        var steps = new List<NoteStep>();
        var title = "Catatan";
        var blocks = new List<NoteBlock>();
        var explicitStep = false;
        foreach (var block in Markdown.Parse(text, Pipeline))
        {
            if (block is HeadingBlock { Level: 2 } heading)
            {
                if (blocks.Count > 0 || explicitStep) steps.Add(new(title, blocks));
                title = string.Concat(ReadRuns(heading.Inline).Select(run => run.Text)).Trim();
                if (title.Length == 0) title = "Langkah";
                blocks = [];
                explicitStep = true;
            }
            else if (block is HeadingBlock { Level: 1 } documentTitle && !explicitStep && blocks.Count == 0)
            {
                title = string.Concat(ReadRuns(documentTitle.Inline).Select(run => run.Text)).Trim();
            }
            else
            {
                Append(block, blocks);
            }
        }
        if (blocks.Count > 0 || explicitStep || steps.Count == 0)
            steps.Add(new(title.Length == 0 ? "Catatan" : title, blocks));
        return steps;
    }

    private static void Append(Block block, List<NoteBlock> blocks, string prefix = "", int depth = 0)
    {
        switch (block)
        {
            case CodeBlock code:
                blocks.Add(new(NoteBlockKind.Code, [new(code.Lines.ToString().TrimEnd('\r', '\n'), Monospace: true)]));
                break;
            case ListBlock list when depth < 12:
                var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                foreach (var child in list.OfType<ListItemBlock>())
                {
                    var marker = new string(' ', depth * 2) + (list.IsOrdered ? $"{number++}. " : "• ");
                    foreach (var itemBlock in child)
                    {
                        Append(itemBlock, blocks, marker, depth + 1);
                        marker = "";
                    }
                }
                break;
            case LeafBlock leaf when leaf.Inline is not null:
                var runs = ReadRuns(leaf.Inline);
                if (prefix.Length > 0) runs.Insert(0, new(prefix));
                blocks.Add(new(block is HeadingBlock ? NoteBlockKind.Heading : NoteBlockKind.Text, runs));
                break;
            case QuoteBlock quote when depth < 12:
                foreach (var child in quote) Append(child, blocks, "› ", depth + 1);
                break;
        }
    }

    private static List<NoteRun> ReadRuns(ContainerInline? container)
    {
        var runs = new List<NoteRun>();
        if (container is not null) AppendInline(container, runs, false);
        return runs;
    }

    private static void AppendInline(Inline node, List<NoteRun> runs, bool bold)
    {
        switch (node)
        {
            case LiteralInline literal:
                runs.Add(new(literal.Content.ToString(), bold));
                break;
            case HtmlEntityInline entity:
                runs.Add(new(entity.Transcoded.ToString(), bold));
                break;
            case CodeInline code:
                runs.Add(new(code.Content, bold, true));
                break;
            case LineBreakInline line:
                runs.Add(new(line.IsHard ? "\n" : " ", bold));
                break;
            case AutolinkInline link:
                runs.Add(new(link.Url, bold));
                break;
            case ContainerInline container:
                // Links and images are rendered only as their text; never load URLs.
                foreach (var child in container)
                    AppendInline(child, runs, bold || node is EmphasisInline { DelimiterCount: 2 });
                break;
        }
    }
}
