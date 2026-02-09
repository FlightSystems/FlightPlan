namespace FlightPlan.Models;

/// <summary>
/// Manifest file containing all AI-generated augmentations for FlightPlan reports.
/// This file is version-controlled and human-editable.
/// </summary>
public class AiAugmentationManifest
{
    /// <summary>
    /// When these augmentations were generated
    /// </summary>
    public DateTime Generated { get; set; }

    /// <summary>
    /// The AI model used to generate the content (e.g., "llama3.2", "deepseek-r1:8b")
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the compiled flight plan to detect when regeneration is needed
    /// </summary>
    public string? PlanHash { get; set; }

    /// <summary>
    /// Augmentations grouped by report type, then by section
    /// </summary>
    public Dictionary<string, List<AiAugmentation>> Augmentations { get; set; } = new();
}
