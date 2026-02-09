using System.Text.Json.Serialization;

namespace FlightPlan.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FindingSeverity
{
    Info,
    Low,
    Medium,
    High
}

public sealed class Finding
{
    public string Id { get; init; } = string.Empty;

    public string ReportId { get; init; } = string.Empty;

    /// <summary>
    /// Optional: the report section heading where this finding is discussed.
    /// Used for deep-linking (e.g., report.html#section-anchor).
    /// </summary>
    public string? SectionHeading { get; init; }

    /// <summary>
    /// Optional: stable anchor id for the section where this finding is discussed.
    /// Prefer this over <see cref="SectionHeading"/> for deep links so links remain stable
    /// even if the human-readable heading text changes.
    /// </summary>
    public string? SectionAnchorId { get; init; }

    public FindingSeverity Severity { get; init; } = FindingSeverity.Info;

    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string? EntityRef { get; init; }

    public List<string> Evidence { get; init; } = [];
}
