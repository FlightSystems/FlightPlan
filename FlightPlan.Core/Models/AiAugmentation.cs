namespace FlightPlan.Models;

/// <summary>
/// Represents AI-generated content for a specific section of a report.
/// </summary>
public class AiAugmentation
{
    /// <summary>
    /// The section identifier (e.g., "executive-summary", "platform-overview")
    /// </summary>
    public string SectionId { get; set; } = string.Empty;

    /// <summary>
    /// Optional title for callout blocks (e.g., "AI Analysis")
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// The AI-generated narrative content
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Type of augmentation: "narrative" (paragraph) or "callout" (info box)
    /// </summary>
    public string Type { get; set; } = "narrative";
}
