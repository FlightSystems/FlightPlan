using System.Text;

namespace FlightPlan.Reporting;

public sealed class MarkdownRenderer : IReportDocumentRenderer
{
    public string Render(ReportDocument document)
    {
        var sb = new StringBuilder();

        RenderTitle(document, sb);
        RenderMetadata(document, sb);

        foreach (var section in document.Sections)
        {
            RenderSection(section, sb);
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------
    // Document-level
    // ------------------------------------------------------------

    private void RenderTitle(ReportDocument doc, StringBuilder sb)
    {
        sb.AppendLine($"# {doc.Title}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(doc.Subtitle))
        {
            sb.AppendLine($"> {doc.Subtitle}");
            sb.AppendLine();
        }
    }

    private void RenderMetadata(ReportDocument doc, StringBuilder sb)
    {
        if (doc.Metadata == null) return;

        sb.AppendLine("<details>");
        sb.AppendLine("<summary>Metadata</summary>");
        sb.AppendLine();

        sb.AppendLine($"- **Application:** {doc.Metadata.ApplicationName}");
        sb.AppendLine($"- **Version:** {doc.Metadata.Version}");

        foreach (var tag in doc.Metadata.Tags)
        {
            sb.AppendLine($"- **{tag.Key}:** {tag.Value}");
        }

        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
    }

    // ------------------------------------------------------------
    // Sections
    // ------------------------------------------------------------

    private void RenderSection(ReportSection section, StringBuilder sb)
    {
        sb.AppendLine($"{new string('#', section.Level + 1)} {section.Heading}");
        sb.AppendLine();

        foreach (var block in section.Blocks)
        {
            RenderBlock(block, sb);
            sb.AppendLine();
        }

        foreach (var child in section.Children)
        {
            RenderSection(child, sb);
        }
    }

    // ------------------------------------------------------------
    // Blocks
    // ------------------------------------------------------------

    private void RenderBlock(IReportBlock block, StringBuilder sb)
    {
        switch (block)
        {
            case ParagraphBlock p:
                sb.AppendLine(ConvertTermsToMarkdown(p.Text));
                break;

            case BulletListBlock list:
                foreach (var item in list.Items)
                {
                    RenderListItem(item, sb, indentLevel: 0);
                }
                break;

            case TableBlock table:
                RenderTable(table, sb);
                break;

            case KeyValueTableBlock kv:
                RenderKeyValueTable(kv, sb);
                break;

            case CodeBlock code:
                RenderCodeBlock(code, sb);
                break;

            case CalloutBlock callout:
                RenderCallout(callout, sb);
                break;

            case DividerBlock:
                sb.AppendLine("---");
                break;

            default:
                sb.AppendLine($"<!-- Unsupported block: {block.GetType().Name} -->");
                break;
        }
    }

    private static void RenderListItem(ListItem item, StringBuilder sb, int indentLevel)
    {
        var indent = new string(' ', indentLevel * 2);
        sb.AppendLine($"{indent}- {ConvertTermsToMarkdown(item.Text)}");

        if (item.Children.Count == 0)
        {
            return;
        }

        foreach (var child in item.Children)
        {
            RenderListItem(child, sb, indentLevel + 1);
        }
    }

    private static void RenderCodeBlock(CodeBlock code, StringBuilder sb)
    {
        var language = string.IsNullOrWhiteSpace(code.Language) ? string.Empty : code.Language.Trim();
        sb.AppendLine($"```{language}");
        sb.AppendLine(code.Code ?? string.Empty);
        sb.AppendLine("```");
    }

    private static void RenderCallout(CalloutBlock callout, StringBuilder sb)
    {
        var kind = callout.CalloutType.ToString();
        var title = string.IsNullOrWhiteSpace(callout.Title) ? kind : $"{kind}: {callout.Title}";

        sb.AppendLine($"> **{title}**");
        sb.AppendLine(">");

        foreach (var line in (callout.Message ?? string.Empty).Split('\n'))
        {
            sb.AppendLine($"> {line.TrimEnd()}".TrimEnd());
        }
    }

    private void RenderTable(TableBlock table, StringBuilder sb)
    {
        // Header
        sb.Append("| ");
        sb.Append(string.Join(" | ", table.Headers.Select(ConvertTermsToMarkdown)));
        sb.AppendLine(" |");

        // Divider
        sb.Append("| ");
        sb.Append(string.Join(" | ", table.Headers.Select(_ => "---")));
        sb.AppendLine(" |");

        // Rows
        foreach (var row in table.Rows)
        {
            sb.Append("| ");
            sb.Append(string.Join(" | ", row.Cells.Select(ConvertTermsToMarkdown)));
            sb.AppendLine(" |");
        }
    }

    private void RenderKeyValueTable(KeyValueTableBlock table, StringBuilder sb)
    {
        sb.AppendLine("| Key | Value |");
        sb.AppendLine("| --- | ----- |");

        foreach (var row in table.Rows)
        {
            sb.AppendLine($"| {ConvertTermsToMarkdown(row.Key)} | {ConvertTermsToMarkdown(row.Value)} |");
        }
    }

    private static string ConvertTermsToMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        // Convert term hint tokens: [[term|label|description]] -> "label — description"
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\[\[term\|(.+?)\|(.+?)\]\]",
            match =>
            {
                var label = match.Groups[1].Value?.Trim() ?? string.Empty;
                var description = match.Groups[2].Value?.Trim() ?? string.Empty;
                return string.IsNullOrWhiteSpace(description) ? label : $"{label} — {description}";
            });
    }
}

