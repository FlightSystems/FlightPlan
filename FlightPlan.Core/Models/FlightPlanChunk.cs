namespace FlightPlan.Models;

/// <summary>
/// Represents a semantic chunk of the FlightPlan with its vector embedding
/// </summary>
public class FlightPlanChunk
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = new();
    public float[] Embedding { get; set; } = Array.Empty<float>();
    
    /// <summary>
    /// Source file path (for documentation chunks)
    /// </summary>
    public string? SourceFile { get; set; }
    
    /// <summary>
    /// Section/heading within the source file
    /// </summary>
    public string? SourceSection { get; set; }
    
    /// <summary>
    /// Similarity score (set during search, not persisted)
    /// </summary>
    public float Score { get; set; }
}
