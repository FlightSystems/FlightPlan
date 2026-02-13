using System.ComponentModel;
using System.Text.Json;
using Microsoft.SemanticKernel;
using FlightPlan.Models;

namespace FlightPlan.Services;

/// <summary>
/// Service that exposes FlightPlan MCP tools as Semantic Kernel functions for function calling
/// </summary>
public class McpToolService
{
    private readonly string _storeType;
    private readonly string? _connectionString;
    private readonly string? _indexName;

    public McpToolService(string storeType = "json", string? connectionString = null, string? indexName = null)
    {
        _storeType = storeType;
        _connectionString = connectionString;
        _indexName = indexName;
    }

    /// <summary>
    /// Add FlightPlan MCP tools as Kernel plugins
    /// </summary>
    public void RegisterTools(Kernel kernel)
    {
        // Register documentation query tool
        kernel.Plugins.AddFromObject(this, "FlightPlanMcp");
    }

    [KernelFunction("query_documentation")]
    [Description("Search and query the FlightPlan documentation using AI-powered semantic search. Returns relevant documentation with citations about the FlightPlan architecture, services, resources, teams, and best practices.")]
    public async Task<string> QueryDocumentation(
        [Description("The question or search query about the FlightPlan architecture and documentation")] 
        string query)
    {
        try
        {
            var store = VectorStoreFactory.Create(_storeType, _connectionString, _indexName);
            
            if (!store.IndexExists())
            {
                return $"Error: No index found for {_storeType} store. Please run 'flight index' first.";
            }

            var chunks = await store.Search(query, "http://localhost:11434", "nomic-embed-text", 5);
            
            if (chunks.Count == 0)
            {
                return "No relevant documentation found for your query.";
            }

            var result = new System.Text.StringBuilder();
            result.AppendLine("## Documentation Results\n");
            
            foreach (var chunk in chunks.Take(3)) // Limit to top 3 for token efficiency
            {
                result.AppendLine($"### {chunk.Type}: {chunk.Id}");
                if (!string.IsNullOrEmpty(chunk.SourceFile))
                {
                    result.AppendLine($"**Source:** {chunk.SourceFile}");
                }
                if (!string.IsNullOrEmpty(chunk.SourceSection))
                {
                    result.AppendLine($"**Section:** {chunk.SourceSection}");
                }
                result.AppendLine($"**Relevance Score:** {chunk.Score:F3}\n");
                result.AppendLine(chunk.Content);
                result.AppendLine("\n---\n");
            }

            return result.ToString();
        }
        catch (Exception ex)
        {
            return $"Error querying documentation: {ex.Message}";
        }
    }

    [KernelFunction("list_documents")]
    [Description("List all available markdown documentation files in the FlightPlan documentation system.")]
    public async Task<string> ListDocuments()
    {
        try
        {
            var docsPath = Path.Combine(Directory.GetCurrentDirectory(), "../docs/flightplan");
            if (!Directory.Exists(docsPath))
            {
                docsPath = Path.Combine(Directory.GetCurrentDirectory(), "docs/flightplan");
            }

            if (!Directory.Exists(docsPath))
            {
                return "Documentation directory not found";
            }

            var mdFiles = Directory.GetFiles(docsPath, "*.md", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(docsPath, f))
                .OrderBy(f => f)
                .ToList();

            return $"Available documentation files ({mdFiles.Count}):\n\n" + string.Join("\n", mdFiles.Select(f => $"- {f}"));
        }
        catch (Exception ex)
        {
            return $"Error listing documents: {ex.Message}";
        }
    }

    [KernelFunction("query_graph")]
    [Description("Execute a Cypher query against the FlightPlan graph database to analyze services, dependencies, teams, and resources. Use this for complex queries about system architecture and relationships. Only available when using FalkorDB store.")]
    public async Task<string> QueryGraph(
        [Description("Cypher query to execute (e.g., MATCH (s:Service) RETURN s.name LIMIT 10)")] 
        string query,
        [Description("Optional parameters for the query (use $paramName in query)")]
        string? parametersJson = null)
    {
        if (_storeType != "falkordb")
        {
            return "Error: Graph queries require FalkorDB store. Current store: " + _storeType;
        }

        try
        {
            var store = VectorStoreFactory.Create(_storeType, _connectionString, _indexName) as FalkorDBVectorStore;
            if (store == null)
            {
                return "Error: Failed to create FalkorDB store";
            }

            Dictionary<string, object>? parameters = null;
            if (!string.IsNullOrWhiteSpace(parametersJson))
            {
                parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(parametersJson);
            }

            var result = await store.ExecuteCypherQuery(query, parameters);
            
            if (result.Success)
            {
                var text = $"Query returned {result.RowCount} rows in {result.ExecutionTimeMs:F2}ms:\n\n";
                text += JsonSerializer.Serialize(result.Results, new JsonSerializerOptions { WriteIndented = true });
                return text;
            }
            else
            {
                return $"Query failed: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            return $"Error executing graph query: {ex.Message}";
        }
    }

    [KernelFunction("get_graph_statistics")]
    [Description("Get statistics about the FlightPlan graph including node counts and relationship counts by type. Only available when using FalkorDB store.")]
    public async Task<string> GetGraphStatistics()
    {
        if (_storeType != "falkordb")
        {
            return "Error: Graph statistics require FalkorDB store. Current store: " + _storeType;
        }

        try
        {
            var store = VectorStoreFactory.Create(_storeType, _connectionString, _indexName) as FalkorDBVectorStore;
            if (store == null)
            {
                return "Error: Failed to create FalkorDB store";
            }

            var stats = await store.GetGraphStatistics();
            var text = $"## Graph Statistics\n\n" +
                      $"**Total Nodes:** {stats.NodeCount}\n" +
                      $"**Total Relationships:** {stats.RelationshipCount}\n\n" +
                      $"### Nodes by Label:\n" +
                      string.Join("\n", stats.NodesByLabel.Select(kvp => $"- {kvp.Key}: {kvp.Value}")) +
                      $"\n\n### Relationships by Type:\n" +
                      string.Join("\n", stats.RelationshipsByType.Select(kvp => $"- {kvp.Key}: {kvp.Value}"));

            return text;
        }
        catch (Exception ex)
        {
            return $"Error getting graph statistics: {ex.Message}";
        }
    }

    [KernelFunction("analyze_service_dependencies")]
    [Description("Analyze dependencies for a specific service, including what it depends on and what depends on it. Optionally include impact analysis showing all services that would be affected if this service fails.")]
    public async Task<string> AnalyzeServiceDependencies(
        [Description("The service ID to analyze")] 
        string serviceId,
        [Description("Include impact analysis (all services that would be affected if this service fails)")]
        bool includeImpact = false)
    {
        if (_storeType != "falkordb")
        {
            return "Error: Dependency analysis requires FalkorDB store. Current store: " + _storeType;
        }

        try
        {
            var store = VectorStoreFactory.Create(_storeType, _connectionString, _indexName) as FalkorDBVectorStore;
            if (store == null)
            {
                return "Error: Failed to create FalkorDB store";
            }

            var dependencies = await store.GetServiceDependencies(serviceId);
            var dependents = await store.GetServiceDependents(serviceId);
            
            var text = $"## Service Dependency Analysis: {serviceId}\n\n";
            text += $"### Dependencies ({dependencies.Count})\n";
            text += $"Services that **{serviceId}** depends on:\n\n";
            
            if (dependencies.Any())
            {
                foreach (var dep in dependencies)
                {
                    var name = dep.Properties.GetValueOrDefault("name", dep.Id)?.ToString() ?? dep.Id;
                    text += $"- **{name}** (`{dep.Id}`)\n";
                }
            }
            else
            {
                text += "- None found\n";
            }
            
            text += $"\n### Dependents ({dependents.Count})\n";
            text += $"Services that depend on **{serviceId}**:\n\n";
            
            if (dependents.Any())
            {
                foreach (var dep in dependents)
                {
                    var name = dep.Properties.GetValueOrDefault("name", dep.Id)?.ToString() ?? dep.Id;
                    text += $"- **{name}** (`{dep.Id}`)\n";
                }
            }
            else
            {
                text += "- None found\n";
            }

            if (includeImpact)
            {
                var impact = await store.GetImpactAnalysis(serviceId);
                text += $"\n### Impact Analysis ({impact.Count})\n";
                text += $"All services affected if **{serviceId}** fails:\n\n";
                
                if (impact.Any())
                {
                    foreach (var node in impact)
                    {
                        text += $"- **{node.Id}** ({node.Label})\n";
                    }
                }
                else
                {
                    text += "- No services would be directly impacted\n";
                }
            }

            return text;
        }
        catch (Exception ex)
        {
            return $"Error analyzing service dependencies: {ex.Message}";
        }
    }

    [KernelFunction("get_team_ownership")]
    [Description("Get all services and resources owned by a specific team.")]
    public async Task<string> GetTeamOwnership(
        [Description("The team ID to query")] 
        string teamId)
    {
        if (_storeType != "falkordb")
        {
            return "Error: Team ownership queries require FalkorDB store. Current store: " + _storeType;
        }

        try
        {
            var store = VectorStoreFactory.Create(_storeType, _connectionString, _indexName) as FalkorDBVectorStore;
            if (store == null)
            {
                return "Error: Failed to create FalkorDB store";
            }

            var ownership = await store.GetTeamOwnership(teamId);
            
            var text = $"## Team Ownership: {teamId}\n\n";
            
            if (ownership.TryGetValue("services", out var services) && services.Any())
            {
                text += $"### Services ({services.Count})\n\n";
                foreach (var node in services)
                {
                    var name = node.Properties.GetValueOrDefault("name", node.Id)?.ToString() ?? node.Id;
                    text += $"- **{name}** (`{node.Id}`)\n";
                }
                text += "\n";
            }
            else
            {
                text += "### Services\n\n- None found\n\n";
            }
            
            if (ownership.TryGetValue("resources", out var resources) && resources.Any())
            {
                text += $"### Resources ({resources.Count})\n\n";
                foreach (var node in resources)
                {
                    var name = node.Properties.GetValueOrDefault("name", node.Id)?.ToString() ?? node.Id;
                    text += $"- **{name}** (`{node.Id}`)\n";
                }
                text += "\n";
            }
            else
            {
                text += "### Resources\n\n- None found\n\n";
            }

            return text;
        }
        catch (Exception ex)
        {
            return $"Error getting team ownership: {ex.Message}";
        }
    }
}
