using System.Net;

namespace FlightPlan.Reporting;

public static class PublishedReportsTableOfContentsGenerator
{
    public static ReportDocument Generate(
        CompiledFlightPlan plan,
        string format,
        IReadOnlyList<Finding> findings,
        string compiledFileName,
        string findingsFileName,
        string architectureFileName,
        string securityFileName,
        string onboardingFileName,
        string serviceCatalogFileName,
        string resourceCatalogFileName)
    {
        var isHtml = format.Equals("html", StringComparison.OrdinalIgnoreCase);

        var reportFileById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["architecture-overview"] = architectureFileName,
            ["security-overview"] = securityFileName,
            ["developer-onboarding"] = onboardingFileName,
            ["service-catalog"] = serviceCatalogFileName,
            ["resource-catalog"] = resourceCatalogFileName
        };

        var reportsTable = new TableBlock
        {
            Headers = { "Report", "What it is" },
            Rows =
            {
                new()
                {
                    Cells =
                    {
                        FormatLink(isHtml, architectureFileName, "Architecture"),
                        "Design-time architecture overview",
                    }
                },
                new()
                {
                    Cells =
                    {
                        FormatLink(isHtml, securityFileName, "Security"),
                        "Design-time security/compliance overview",
                    }
                },
                new()
                {
                    Cells =
                    {
                        FormatLink(isHtml, onboardingFileName, "Onboarding"),
                        "Developer orientation guide (entry points, owners, repos, deployment processes)",
                    }
                },
                new()
                {
                    Cells =
                    {
                        FormatLink(isHtml, serviceCatalogFileName, "Service Catalog"),
                        "Detailed inventory of services, interfaces, and dependencies",
                    }
                },
                new()
                {
                    Cells =
                    {
                        FormatLink(isHtml, resourceCatalogFileName, "Resource Catalog"),
                        "Detailed inventory of resources, consumers, and configuration",
                    }
                }
            }
        };

        var machineReadableTable = new TableBlock
        {
            Headers = { "Output", "What it is" },
            Rows =
            {
                new()
                {
                    Cells =
                    {
                        FormatLink(isHtml, compiledFileName, "Compiled Plan"),
                        "Machine-readable compiled plan (build output)",
                    }
                },
                new()
                {
                    Cells =
                    {
                        FormatLink(isHtml, findingsFileName, "Findings"),
                        "Aggregated machine-readable findings across all reports",
                    }
                }
            }
        };

        return new ReportDocument
        {
            Title = "Published Flight Plan Reports",
            Subtitle = "Table of contents for generated outputs",
            Metadata = new ReportMetadata
            {
                ApplicationName = plan.Application?.Name,
                Tags =
                {
                    ["format"] = isHtml ? "html" : "text"
                }
            },
            Sections =
            {
                new ReportSection
                {
                    Heading = "Overview",
                    Level = 1,
                    Blocks =
                    {
                        new ParagraphBlock
                        {
                            Text =
                                "This publish output contains a compiled plan plus multiple reports. " +
                                "Use the links below to navigate."
                        }
                    }
                },
                new ReportSection
                {
                    Heading = "Reports",
                    Level = 2,
                    Blocks =
                    {
                        reportsTable
                    }
                },
                new ReportSection
                {
                    Heading = "Machine-Readable Output",
                    Level = 2,
                    Blocks =
                    {
                        machineReadableTable
                    }
                },
                BuildFindingsSection(findings, isHtml, reportFileById)
            }
        };
    }

    private static ReportSection BuildFindingsSection(
        IReadOnlyList<Finding> findings,
        bool isHtml,
        IReadOnlyDictionary<string, string> reportFileById)
    {
        var severityOrder = new Dictionary<FindingSeverity, int>
        {
            [FindingSeverity.High] = 0,
            [FindingSeverity.Medium] = 1,
            [FindingSeverity.Low] = 2,
            [FindingSeverity.Info] = 3,
        };

        var high = findings.Count(f => f.Severity == FindingSeverity.High);
        var medium = findings.Count(f => f.Severity == FindingSeverity.Medium);
        var low = findings.Count(f => f.Severity == FindingSeverity.Low);
        var info = findings.Count(f => f.Severity == FindingSeverity.Info);

        var section = new ReportSection
        {
            Heading = "Findings",
            Level = 1,
        };

        if (findings.Count == 0)
        {
            section.Blocks.Add(new ParagraphBlock { Text = "No findings were produced by the report generators." });
            return section;
        }

        section.Blocks.Add(new CalloutBlock
        {
            CalloutType = CalloutKind.Info,
            Title = "Aggregated executive summary",
            Message = $"Total: {findings.Count} (High: {high}, Medium: {medium}, Low: {low}, Info: {info})."
        });

        var items = findings
            .OrderBy(f => severityOrder.GetValueOrDefault(f.Severity, 999))
            .ThenBy(f => f.ReportId)
            .ThenBy(f => f.Title)
            .Take(12)
            .Select(f => new ListItem
            {
                Text = FormatFindingListItemText(f, isHtml, reportFileById)
            })
            .ToList();

        section.Blocks.Add(new BulletListBlock { Items = items });
        section.Blocks.Add(new ParagraphBlock { Text = "See the Findings JSON output for full detail and evidence." });

        return section;
    }

    private static string FormatFindingListItemText(
        Finding finding,
        bool isHtml,
        IReadOnlyDictionary<string, string> reportFileById)
    {
        var severity = finding.Severity.ToString();
        var reportId = finding.ReportId ?? string.Empty;
        var title = finding.Title ?? string.Empty;
        var summary = finding.Summary ?? string.Empty;

        var reportLink = FormatReportIdLink(isHtml, reportId, finding.SectionAnchorId, finding.SectionHeading, reportFileById);

        if (!isHtml)
            return $"[{severity}] ({reportLink}) {title}: {summary}";

        // HtmlTailwindRenderer does not HTML-escape content; do so here.
        var tag = FormatSeverityTag(finding.Severity);
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedSummary = WebUtility.HtmlEncode(summary);

        return $"{tag} <span class=\"text-gray-500\">({reportLink})</span> <span class=\"font-medium\">{encodedTitle}</span><span class=\"text-gray-700\">: {encodedSummary}</span>";
    }

    private static string FormatReportIdLink(
        bool isHtml,
        string reportId,
        string? sectionAnchorId,
        string? sectionHeading,
        IReadOnlyDictionary<string, string> reportFileById)
    {
        var trimmed = (reportId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        if (!reportFileById.TryGetValue(trimmed, out var fileName) || string.IsNullOrWhiteSpace(fileName))
        {
            return isHtml ? WebUtility.HtmlEncode(trimmed) : trimmed;
        }

        var anchor = !string.IsNullOrWhiteSpace(sectionAnchorId)
            ? sectionAnchorId.Trim()
            : ToAnchorId(sectionHeading);
        var href = anchor is null ? fileName : $"{fileName}#{anchor}";

        if (isHtml)
        {
            var encodedReportId = WebUtility.HtmlEncode(trimmed);
            var encodedHref = WebUtility.HtmlEncode(href);
            return $"<a class=\"text-blue-600 underline\" href=\"{encodedHref}\">{encodedReportId}</a>";
        }

        return $"[{trimmed}]({href})";
    }

    private static string? ToAnchorId(string? heading)
    {
        var text = (heading ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var sb = new System.Text.StringBuilder(text.Length);
        var prevDash = false;
        foreach (var ch in text)
        {
            var isAlnum = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');
            if (isAlnum)
            {
                sb.Append(ch);
                prevDash = false;
                continue;
            }

            if (!prevDash)
            {
                sb.Append('-');
                prevDash = true;
            }
        }

        var result = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string FormatSeverityTag(FindingSeverity severity)
    {
        var (bg, fg, ring) = severity switch
        {
            FindingSeverity.High => ("bg-red-100", "text-red-800", "ring-red-200"),
            FindingSeverity.Medium => ("bg-amber-100", "text-amber-800", "ring-amber-200"),
            FindingSeverity.Low => ("bg-blue-100", "text-blue-800", "ring-blue-200"),
            _ => ("bg-gray-100", "text-gray-800", "ring-gray-200")
        };

        var label = WebUtility.HtmlEncode(severity.ToString());
        return $"<span class=\"inline-flex items-center px-2 py-0.5 rounded text-xs font-semibold {bg} {fg} ring-1 {ring}\">{label}</span>";
    }

    private static string FormatLink(bool isHtml, string fileName, string title)
    {
        if (isHtml)
            return $"<a class=\"text-blue-600 underline\" href=\"{fileName}\">{title}</a>";

        return $"[{title}]({fileName})";
    }
}
