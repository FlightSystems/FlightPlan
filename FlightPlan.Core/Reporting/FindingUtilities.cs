using System.Security.Cryptography;
using System.Text;
using FlightPlan.Models;

namespace FlightPlan.Reporting;

public static class FindingUtilities
{
    public static List<Finding> NormalizeAndDeduplicate(IEnumerable<Finding> findings)
    {
        var normalized = findings
            .Where(f => f is not null)
            .Select(Normalize)
            .ToList();

        // Group by stable "meaning". (Summary is intentionally excluded; it may contain counts.)
        var grouped = normalized
            .GroupBy(
                f => BuildKey(f.ReportId, f.Title, f.EntityRef),
                StringComparer.Ordinal);

        var result = new List<Finding>();
        foreach (var g in grouped)
        {
            var groupList = g.ToList();
            var maxSeverity = groupList.Max(f => GetSeverityRank(f.Severity));

            var representative = groupList
                .OrderByDescending(f => GetSeverityRank(f.Severity))
                .ThenByDescending(f => (f.Summary ?? string.Empty).Length)
                .First();

            var mergedEvidence = groupList
                .SelectMany(f => f.Evidence ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s)
                .ToList();

            var merged = new Finding
            {
                Id = representative.Id,
                ReportId = representative.ReportId,
                SectionHeading = representative.SectionHeading,
                SectionAnchorId = representative.SectionAnchorId,
                Severity = FromSeverityRank(maxSeverity),
                Title = representative.Title,
                Summary = representative.Summary,
                EntityRef = representative.EntityRef,
                Evidence = mergedEvidence
            };

            result.Add(merged);
        }

        return result
            .OrderByDescending(f => GetSeverityRank(f.Severity))
            .ThenBy(f => f.ReportId)
            .ThenBy(f => f.Title)
            .ToList();
    }

    private static string BuildKey(string reportId, string title, string? entityRef)
    {
        // Use a separator that is extremely unlikely to appear in user content.
        return string.Concat(reportId ?? string.Empty, "\u001f", title ?? string.Empty, "\u001f", entityRef ?? string.Empty);
    }

    private static Finding Normalize(Finding f)
    {
        var reportId = (f.ReportId ?? string.Empty).Trim();
        var sectionHeading = string.IsNullOrWhiteSpace(f.SectionHeading) ? null : f.SectionHeading.Trim();
        var sectionAnchorId = string.IsNullOrWhiteSpace(f.SectionAnchorId) ? null : f.SectionAnchorId.Trim();
        var title = (f.Title ?? string.Empty).Trim();
        var summary = (f.Summary ?? string.Empty).Trim();
        var entityRef = string.IsNullOrWhiteSpace(f.EntityRef) ? null : f.EntityRef.Trim();

        var id = string.IsNullOrWhiteSpace(f.Id)
            ? CreateStableId(reportId, f.Severity, title, entityRef, summary)
            : f.Id.Trim();

        var evidence = (f.Evidence ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s)
            .ToList();

        return new Finding
        {
            Id = id,
            ReportId = reportId,
            SectionHeading = sectionHeading,
            SectionAnchorId = sectionAnchorId,
            Severity = f.Severity,
            Title = title,
            Summary = summary,
            EntityRef = entityRef,
            Evidence = evidence
        };
    }

    private static string CreateStableId(string reportId, FindingSeverity severity, string title, string? entityRef, string summary)
    {
        // Deterministic ID across runs: include the semantic components.
        // Summary is included to reduce accidental collisions for same Title.
        var canonical = new StringBuilder()
            .Append(reportId).Append('\n')
            .Append(severity).Append('\n')
            .Append(title).Append('\n')
            .Append(entityRef ?? string.Empty).Append('\n')
            .Append(summary)
            .ToString();

        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);

        // hex (lowercase)
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int GetSeverityRank(FindingSeverity severity)
    {
        return severity switch
        {
            FindingSeverity.High => 3,
            FindingSeverity.Medium => 2,
            FindingSeverity.Low => 1,
            _ => 0
        };
    }

    private static FindingSeverity FromSeverityRank(int rank)
    {
        return rank switch
        {
            >= 3 => FindingSeverity.High,
            2 => FindingSeverity.Medium,
            1 => FindingSeverity.Low,
            _ => FindingSeverity.Info
        };
    }
}
