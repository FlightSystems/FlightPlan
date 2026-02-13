using System.Text.Json.Serialization;

namespace FlightPlan.Models;

public class GraphQueryRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;
    
    [JsonPropertyName("parameters")]
    public Dictionary<string, object>? Parameters { get; set; }
}

public class GraphQueryResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("results")]
    public List<Dictionary<string, object>>? Results { get; set; }
    
    [JsonPropertyName("error")]
    public string? Error { get; set; }
    
    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }
    
    [JsonPropertyName("executionTime")]
    public double ExecutionTimeMs { get; set; }
}

public class GraphPattern
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CypherQuery { get; set; } = string.Empty;
    public List<string> RequiredParameters { get; set; } = new();
}

public class GraphNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
    
    [JsonPropertyName("properties")]
    public Dictionary<string, object> Properties { get; set; } = new();
}

public class GraphRelationship
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;
    
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;
    
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
    
    [JsonPropertyName("properties")]
    public Dictionary<string, object>? Properties { get; set; }
}

public class GraphStatistics
{
    [JsonPropertyName("nodeCount")]
    public int NodeCount { get; set; }
    
    [JsonPropertyName("relationshipCount")]
    public int RelationshipCount { get; set; }
    
    [JsonPropertyName("nodesByLabel")]
    public Dictionary<string, int> NodesByLabel { get; set; } = new();
    
    [JsonPropertyName("relationshipsByType")]
    public Dictionary<string, int> RelationshipsByType { get; set; } = new();
}
