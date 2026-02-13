namespace FlightPlan.Services;

/// <summary>
/// Factory for creating vector store instances based on store type
/// </summary>
public static class VectorStoreFactory
{
    /// <summary>
    /// Create a vector store instance
    /// </summary>
    /// <param name="storeType">Type of store: "json" or "falkordb"</param>
    /// <param name="connectionString">Connection string (for falkordb: "host:port", for json: file path)</param>
    /// <param name="graphName">Graph name for FalkorDB (default: "flightplan")</param>
    /// <returns>Vector store instance</returns>
    public static IVectorStore Create(string storeType, string? connectionString = null, string? graphName = null)
    {
        return storeType.ToLowerInvariant() switch
        {
            "json" => new VectorStore(connectionString ?? ".flightplan.index.json"),
            "falkordb" => new FalkorDBVectorStore(
                connectionString ?? "localhost:6379",
                graphName ?? "flightplan"
            ),
            _ => throw new ArgumentException($"Unknown store type: {storeType}. Supported: json, falkordb")
        };
    }

    /// <summary>
    /// Get list of supported store types
    /// </summary>
    public static string[] SupportedTypes => new[] { "json", "falkordb" };

    /// <summary>
    /// Check if a store type is supported
    /// </summary>
    public static bool IsSupported(string storeType)
    {
        return SupportedTypes.Contains(storeType.ToLowerInvariant());
    }
}
