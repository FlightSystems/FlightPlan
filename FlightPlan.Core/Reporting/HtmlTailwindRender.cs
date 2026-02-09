using System.Text;
using System.Text.RegularExpressions;
using System.Net;

namespace FlightPlan.Reporting;

public sealed class HtmlTailwindRenderer : IReportDocumentRenderer
{
    public string Render(ReportDocument document)
    {
        var sb = new StringBuilder();
        var headingIdCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        var usesMermaid = DocumentUsesMermaid(document);

        RenderHtmlHeader(document, sb, usesMermaid);

        sb.AppendLine("<body class=\"bg-gray-50 text-gray-900\">");
        sb.AppendLine("<main class=\"max-w-6xl mx-auto p-8\">");

        RenderTitle(document, sb);
        RenderMetadata(document, sb);

        foreach (var section in document.Sections)
        {
            RenderSection(section, sb, headingIdCounts);
        }

        sb.AppendLine("</main>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    // ------------------------------------------------------------
    // HTML scaffolding
    // ------------------------------------------------------------

    private void RenderHtmlHeader(ReportDocument doc, StringBuilder sb, bool usesMermaid)
    {
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\" />");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
        sb.AppendLine($"<title>{doc.Title}</title>");
        sb.AppendLine("<script src=\"https://cdn.tailwindcss.com\"></script>");

        if (usesMermaid)
        {
            // Mermaid is supported by GitHub-flavored Markdown and can be rendered client-side for HTML output.
            sb.AppendLine("<script src=\"https://cdn.jsdelivr.net/npm/mermaid@10/dist/mermaid.min.js\"></script>");
            sb.AppendLine("<script>window.addEventListener('load', () => { mermaid.initialize({ startOnLoad: true, securityLevel: 'strict' }); });</script>");
        }

        sb.AppendLine("</head>");
    }

    private static bool DocumentUsesMermaid(ReportDocument doc)
    {
        foreach (var section in doc.Sections)
        {
            if (SectionUsesMermaid(section)) return true;
        }

        return false;
    }

    private static bool SectionUsesMermaid(ReportSection section)
    {
        foreach (var block in section.Blocks)
        {
            if (block is CodeBlock code &&
                string.Equals(code.Language?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var child in section.Children)
        {
            if (SectionUsesMermaid(child)) return true;
        }

        return false;
    }

    // ------------------------------------------------------------
    // Document-level
    // ------------------------------------------------------------

    private void RenderTitle(ReportDocument doc, StringBuilder sb)
    {
        sb.AppendLine($"<h1 class=\"text-4xl font-bold mb-2\">{doc.Title}</h1>");

        if (!string.IsNullOrWhiteSpace(doc.Subtitle))
        {
            sb.AppendLine($"<p class=\"text-lg text-gray-600 mb-6\">{doc.Subtitle}</p>");
        }
    }

    private void RenderMetadata(ReportDocument doc, StringBuilder sb)
    {
        if (doc.Metadata == null) return;

        sb.AppendLine("<div class=\"bg-white shadow rounded-lg p-4 mb-8\">");
        sb.AppendLine("<dl class=\"grid grid-cols-2 gap-4\">");

        sb.AppendLine($"<div><dt class=\"font-semibold\">Application</dt><dd>{doc.Metadata.ApplicationName}</dd></div>");
        sb.AppendLine($"<div><dt class=\"font-semibold\">Version</dt><dd>{doc.Metadata.Version}</dd></div>");

        foreach (var tag in doc.Metadata.Tags)
        {
            sb.AppendLine($"<div><dt class=\"font-semibold\">{tag.Key}</dt><dd>{tag.Value}</dd></div>");
        }

        sb.AppendLine("</dl>");
        sb.AppendLine("</div>");
    }

    // ------------------------------------------------------------
    // Sections
    // ------------------------------------------------------------

    private void RenderSection(ReportSection section, StringBuilder sb, Dictionary<string, int> headingIdCounts)
    {
        sb.AppendLine($"<section class=\"mb-10\">");
        sb.AppendLine(RenderHeading(section, headingIdCounts));

        foreach (var block in section.Blocks)
        {
            RenderBlock(block, sb);
        }

        foreach (var child in section.Children)
        {
            RenderSection(child, sb, headingIdCounts);
        }

        sb.AppendLine("</section>");
    }

    private static string RenderHeading(ReportSection section, Dictionary<string, int> headingIdCounts)
    {
        var size = section.Level switch
        {
            1 => "text-2xl",
            2 => "text-xl",
            3 => "text-lg",
            _ => "text-base"
        };

        var baseId = string.IsNullOrWhiteSpace(section.Anchor)
            ? ToAnchorId(section.Heading)
            : section.Anchor!.Trim();
        var uniqueId = baseId;
        if (headingIdCounts.TryGetValue(baseId, out var count))
        {
            if (!string.IsNullOrWhiteSpace(section.Anchor))
                throw new InvalidOperationException($"Duplicate explicit section anchor '{baseId}' for heading '{section.Heading}'. Explicit anchors must be unique within a report.");

            count++;
            headingIdCounts[baseId] = count;
            uniqueId = $"{baseId}-{count}";
        }
        else
        {
            headingIdCounts[baseId] = 0;
        }

        return $"<h{section.Level + 1} id=\"{uniqueId}\" class=\"{size} font-semibold mb-4\">{section.Heading}</h{section.Level + 1}>";
    }

    private static readonly Regex AnchorNonAlnum = new("[^a-z0-9]+", RegexOptions.Compiled);

    private static string ToAnchorId(string? heading)
    {
        var text = (heading ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return "section";

        text = AnchorNonAlnum.Replace(text, "-");
        text = text.Trim('-');
        return string.IsNullOrWhiteSpace(text) ? "section" : text;
    }

    // ------------------------------------------------------------
    // Blocks
    // ------------------------------------------------------------

    private void RenderBlock(IReportBlock block, StringBuilder sb)
    {
        switch (block)
        {
            case ParagraphBlock p:
                sb.AppendLine($"<p class=\"mb-4\">{ConvertMarkdownLinksToHtml(p.Text)}</p>");
                break;

            case BulletListBlock list:
                RenderBulletList(list, sb);
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
                sb.AppendLine("<hr class=\"my-6 border-gray-200\" />");
                break;

            default:
                sb.AppendLine($"<!-- Unsupported block: {block.GetType().Name} -->");
                break;
        }
    }

    private static void RenderBulletList(BulletListBlock list, StringBuilder sb)
    {
        sb.AppendLine("<ul class=\"list-disc ml-6 mb-4\">");
        foreach (var item in list.Items)
        {
            RenderListItem(item, sb);
        }
        sb.AppendLine("</ul>");
    }

    private static void RenderListItem(ListItem item, StringBuilder sb)
    {
        sb.AppendLine("<li>");
        sb.AppendLine(ConvertMarkdownLinksToHtml(item.Text));

        if (item.Children.Count > 0)
        {
            sb.AppendLine("<ul class=\"list-disc ml-6 mt-2\">");
            foreach (var child in item.Children)
            {
                RenderListItem(child, sb);
            }
            sb.AppendLine("</ul>");
        }

        sb.AppendLine("</li>");
    }

    private static void RenderCodeBlock(CodeBlock code, StringBuilder sb)
    {
        if (string.Equals(code.Language?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase))
        {
            // Mermaid expects raw (unescaped) graph definitions in an element with class 'mermaid'.
            sb.AppendLine("<pre class=\"mermaid mb-6 p-4 bg-white rounded border overflow-x-auto text-sm\">");
            sb.AppendLine(code.Code ?? string.Empty);
            sb.AppendLine("</pre>");
            return;
        }

        sb.AppendLine("<pre class=\"mb-6 p-4 bg-gray-900 text-gray-100 rounded overflow-x-auto text-sm\"><code>");
        sb.AppendLine(code.Code ?? string.Empty);
        sb.AppendLine("</code></pre>");
    }

    private static void RenderCallout(CalloutBlock callout, StringBuilder sb)
    {
        var (bg, border, titleColor) = callout.CalloutType switch
        {
            CalloutKind.Warning => ("bg-amber-50", "border-amber-300", "text-amber-900"),
            CalloutKind.Error => ("bg-red-50", "border-red-300", "text-red-900"),
            CalloutKind.Success => ("bg-green-50", "border-green-300", "text-green-900"),
            _ => ("bg-blue-50", "border-blue-300", "text-blue-900")
        };

        var title = string.IsNullOrWhiteSpace(callout.Title) ? callout.CalloutType.ToString() : callout.Title;

        sb.AppendLine($"<div class=\"mb-6 p-4 {bg} border-l-4 {border} rounded text-sm\">");
        sb.AppendLine($"<div class=\"font-semibold mb-1 {titleColor}\">{title}</div>");

        foreach (var line in (callout.Message ?? string.Empty).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine("<div class=\"h-2\"></div>");
                continue;
            }

            sb.AppendLine($"<div>{line}</div>");
        }

        sb.AppendLine("</div>");
    }

    private void RenderTable(TableBlock table, StringBuilder sb)
    {
        sb.AppendLine("<div class=\"overflow-x-auto mb-6\">");
        sb.AppendLine("<table class=\"min-w-full bg-white border\">");

        sb.AppendLine("<thead class=\"bg-gray-100\">");
        sb.AppendLine("<tr>");
        foreach (var header in table.Headers)
        {
            sb.AppendLine($"<th class=\"px-4 py-2 text-left border\">{ConvertMarkdownLinksToHtml(header)}</th>");
        }
        sb.AppendLine("</tr>");
        sb.AppendLine("</thead>");

        sb.AppendLine("<tbody>");
        foreach (var row in table.Rows)
        {
            sb.AppendLine("<tr class=\"border-t\">");
            foreach (var cell in row.Cells)
            {
                sb.AppendLine($"<td class=\"px-4 py-2 border\">{ConvertMarkdownLinksToHtml(cell)}</td>");
            }
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody>");

        sb.AppendLine("</table>");
        sb.AppendLine("</div>");
    }

    private void RenderKeyValueTable(KeyValueTableBlock table, StringBuilder sb)
    {
        sb.AppendLine("<div class=\"overflow-x-auto mb-6\">");
        sb.AppendLine("<table class=\"min-w-full bg-white border\">");

        sb.AppendLine("<tbody>");
        foreach (var row in table.Rows)
        {
            sb.AppendLine("<tr class=\"border-t\">");
            sb.AppendLine($"<th class=\"px-4 py-2 text-left font-semibold bg-gray-50 border\">{ConvertMarkdownLinksToHtml(row.Key)}</th>");
            sb.AppendLine($"<td class=\"px-4 py-2 border\">{ConvertMarkdownLinksToHtml(row.Value)}</td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody>");

        sb.AppendLine("</table>");
        sb.AppendLine("</div>");
    }

    /// <summary>
    /// Converts minimal markdown formatting (bold, links) to HTML.
    /// Also adds a small "brand" badge for known repo hosts (e.g., GitHub) based on link URL.
    /// </summary>
    private static string ConvertMarkdownLinksToHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        // Render term hint tokens: [[term|label|description]]
        // This allows report generators to emit hoverable descriptions without embedding raw HTML.
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\[\[term\|(.+?)\|(.+?)\]\]",
            match =>
            {
                var label = match.Groups[1].Value?.Trim() ?? string.Empty;
                var description = match.Groups[2].Value?.Trim() ?? string.Empty;

                var encodedLabel = WebUtility.HtmlEncode(label);
                var encodedDescription = WebUtility.HtmlEncode(description);

                if (string.IsNullOrWhiteSpace(encodedDescription))
                    return encodedLabel;

                return $"<span class=\"inline-flex items-center gap-1\"><span>{encodedLabel}</span><span class=\"text-gray-500\" title=\"{encodedDescription}\">ⓘ</span></span>";
            });

        // Convert **bold** to <strong>
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*([^\*]+)\*\*", "<strong>$1</strong>");
        
        // Convert __bold__ to <strong>
        text = System.Text.RegularExpressions.Regex.Replace(text, @"__([^_]+)__", "<strong>$1</strong>");

        // Convert [link text](url) to <a>
        // NOTE: Use a match evaluator so we can add host-based badges.
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\[([^\]]+)\]\(([^\)]+)\)",
            match =>
            {
                var label = match.Groups[1].Value;
                var url = match.Groups[2].Value;

                var encodedUrl = WebUtility.HtmlEncode(url);
                var encodedLabel = WebUtility.HtmlEncode(label);

                var badge = TryBuildRepoBrandBadgeHtml(url);
                var inner = string.IsNullOrWhiteSpace(badge)
                    ? encodedLabel
                    : $"{badge}<span>{encodedLabel}</span>";

                return $"<a href=\"{encodedUrl}\" class=\"group inline-flex items-center gap-1 text-blue-600 hover:text-blue-800 underline\">{inner}</a>";
            });

        return text;
    }

    private static string? TryBuildRepoBrandBadgeHtml(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var host = (uri.Host ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host)) return null;

        // Monochrome SVG marks for known hosts.
        // Source: Simple Icons (CC0 1.0) https://github.com/simple-icons/simple-icons
        // Note: trademarks may still apply for brand marks.
        return host switch
        {
            "github.com" => BuildBrandIconSvg(
                title: "GitHub",
                pathD: "M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12",
                cssClass: "text-gray-800 group-hover:text-gray-900"),
            "gitlab.com" => BuildBrandIconSvg(
                title: "GitLab",
                pathD: "m23.6004 9.5927-.0337-.0862L20.3.9814a.851.851 0 0 0-.3362-.405.8748.8748 0 0 0-.9997.0539.8748.8748 0 0 0-.29.4399l-2.2055 6.748H7.5375l-2.2057-6.748a.8573.8573 0 0 0-.29-.4412.8748.8748 0 0 0-.9997-.0537.8585.8585 0 0 0-.3362.4049L.4332 9.5015l-.0325.0862a6.0657 6.0657 0 0 0 2.0119 7.0105l.0113.0087.03.0213 4.976 3.7264 2.462 1.8633 1.4995 1.1321a1.0085 1.0085 0 0 0 1.2197 0l1.4995-1.1321 2.4619-1.8633 5.006-3.7489.0125-.01a6.0682 6.0682 0 0 0 2.0094-7.003z",
                cssClass: "text-gray-800 group-hover:text-gray-900"),
            "bitbucket.org" => BuildBrandIconSvg(
                title: "Bitbucket",
                pathD: "M.778 1.213a.768.768 0 00-.768.892l3.263 19.81c.084.5.515.868 1.022.873H19.95a.772.772 0 00.77-.646l3.27-20.03a.768.768 0 00-.768-.891zM14.52 15.53H9.522L8.17 8.466h7.561z",
                cssClass: "text-gray-800 group-hover:text-gray-900"),
            "dev.azure.com" => BuildBrandIconSvg(
                title: "Azure DevOps",
                pathD: "M22.246 9.737a1.292 1.292 0 0 0-1.264-1.02h-8.527L9.06 5.635a.645.645 0 0 0-.874.012L1.985 11.65a.645.645 0 0 0-.192.456v7.916c0 .356.289.645.645.645h4.72a.645.645 0 0 0 .645-.645v-4.733l3.352 3.163a.645.645 0 0 0 .885.006l3.403-3.222h5.54c.65 0 1.203-.466 1.315-1.107l.948-5.392Zm-2.504 2.945h-6.238L7.803 7.3v10.79L4.4 14.875v-2.5l3.403-3.222 3.352 3.163h8.587Z",
                cssClass: "text-gray-800 group-hover:text-gray-900"),
            _ => null
        };
    }

    private static string BuildBrandIconSvg(string title, string pathD, string cssClass)
    {
        // Use currentColor so Tailwind classes control the fill.
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedPath = WebUtility.HtmlEncode(pathD);

        return $"<svg aria-hidden=\"true\" focusable=\"false\" viewBox=\"0 0 24 24\" class=\"w-4 h-4 shrink-0 {cssClass}\" xmlns=\"http://www.w3.org/2000/svg\"><title>{encodedTitle}</title><path fill=\"currentColor\" d=\"{encodedPath}\"/></svg>";
    }
}
