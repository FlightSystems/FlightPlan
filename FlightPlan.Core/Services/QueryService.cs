using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlightPlan.Models;

namespace FlightPlan.Services;

/// <summary>
/// Service for executing FlightPlan queries using RAG (Retrieval-Augmented Generation)
/// Shared between CLI and web interface
/// </summary>
public class QueryService
{
    private readonly string _ollamaUrl;
    private readonly string _model;
    private readonly string _embeddingModel;
    private readonly int _topK;

    public QueryService(
        string ollamaUrl = "http://localhost:11434",
        string model = "llama3.2",
        string embeddingModel = "nomic-embed-text",
        int topK = 5)
    {
        _ollamaUrl = ollamaUrl;
        _model = model;
        _embeddingModel = embeddingModel;
        _topK = topK;
    }

    public async Task<QueryResponse> QueryAsync(string query, int? maxTokens = null, string? baseUrlOverride = null)
    {
        var store = new VectorStore();
        
        if (!store.IndexExists())
        {
            return new QueryResponse
            {
                Success = false,
                Error = "No index found. Please run 'flight index' first."
            };
        }

        try
        {
            // Get metadata for base URL (can be overridden for local serving)
            var metadata = await store.GetMetadata();
            var baseUrl = baseUrlOverride ?? metadata?.BaseUrl;
            
            // Search for relevant chunks
            var relevantChunks = await store.Search(query, _ollamaUrl, _embeddingModel, _topK);
            
            if (relevantChunks.Count == 0)
            {
                return new QueryResponse
                {
                    Success = false,
                    Error = "No relevant context found for your query."
                };
            }

            // Build context from chunks
            var context = BuildContext(relevantChunks, baseUrl);
            
            // Build the system prompt with relevant context only
            var systemPrompt = BuildSystemPrompt(context);

            // Send query to Ollama
            var response = await QueryOllama(_ollamaUrl, _model, systemPrompt, query, maxTokens);
            
            return new QueryResponse
            {
                Success = true,
                Answer = response,
                ChunksFound = relevantChunks.Count,
                TopMatchScore = relevantChunks.Count > 0 ? relevantChunks[0].Score : 0
            };
        }
        catch (HttpRequestException ex)
        {
            return new QueryResponse
            {
                Success = false,
                Error = $"Failed to connect to Ollama at {_ollamaUrl}. Make sure Ollama is running: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new QueryResponse
            {
                Success = false,
                Error = $"Query failed: {ex.Message}"
            };
        }
    }

    public async IAsyncEnumerable<string> QueryStreamingAsync(string query, int? maxTokens = null, string? baseUrlOverride = null)
    {
        var store = new VectorStore();
        
        if (!store.IndexExists())
        {
            yield return "Error: No index found. Please run 'flight index' first.";
            yield break;
        }

        // Get metadata and search (baseUrl can be overridden for local serving)
        var metadata = await store.GetMetadata();
        var baseUrl = baseUrlOverride ?? metadata?.BaseUrl;
        var relevantChunks = await store.Search(query, _ollamaUrl, _embeddingModel, _topK);
        
        if (relevantChunks.Count == 0)
        {
            yield return "Error: No relevant context found for your query.";
            yield break;
        }

        // Build context and prompt
        var context = BuildContext(relevantChunks, baseUrl);
        var systemPrompt = BuildSystemPrompt(context);

        // Stream response
        await foreach (var token in QueryOllamaStreamingAsync(_ollamaUrl, _model, systemPrompt, query, maxTokens))
        {
            yield return token;
        }
    }

    private static string BuildContext(List<FlightPlanChunk> chunks, string? baseUrl)
    {
        var sb = new StringBuilder();
        
        foreach (var chunk in chunks)
        {
            sb.AppendLine($"## {chunk.Type}: {chunk.Id}");
            
            if (!string.IsNullOrEmpty(chunk.SourceFile))
            {
                var sourceFile = chunk.SourceFile;
                
                // For local serving (baseUrl starts with /), strip common documentation prefixes
                if (!string.IsNullOrEmpty(baseUrl) && baseUrl.StartsWith("/"))
                {
                    // Strip "docs/flightplan/" prefix if present (handles both absolute and relative paths)
                    var docsFlightplanIndex = sourceFile.IndexOf("docs/flightplan/", StringComparison.OrdinalIgnoreCase);
                    if (docsFlightplanIndex >= 0)
                    {
                        sourceFile = sourceFile.Substring(docsFlightplanIndex + "docs/flightplan/".Length);
                    }
                }
                
                var sourceReference = !string.IsNullOrEmpty(baseUrl) 
                    ? $"{baseUrl.TrimEnd('/')}/{sourceFile}"
                    : sourceFile;
                    
                sb.AppendLine($"**Source:** {sourceReference}");
                if (!string.IsNullOrEmpty(chunk.SourceSection))
                {
                    sb.AppendLine($"**Section:** {chunk.SourceSection}");
                }
                sb.AppendLine();
            }
            
            sb.AppendLine(chunk.Content);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }
        
        return sb.ToString();
    }
    private static string BuildSystemPrompt(string context)
    {
        return $"""
You are an expert FlightPlan architecture analyst with deep knowledge of cloud-native systems, microservices, and enterprise architecture patterns. 

# Your Role
You help software engineers, architects, and DevOps teams understand their system architecture by providing accurate, insightful analysis based on FlightPlan documentation.

# FlightPlan Knowledge
FlightPlan is an infrastructure-as-code system that defines:
- **Services**: Microservices/applications with owners, platforms, zones, exports, and dependencies
- **Resources**: Shared infrastructure (databases, message queues, storage, etc.)
- **Environments**: Deployment stages (dev, QA, UAT, production)
- **Teams**: Ownership and responsibility boundaries
- **Zones**: Security/network isolation boundaries

Key concepts:
- Services expose "exports" (APIs/endpoints) that other services consume
- Services depend on "resources" (infrastructure components)
- Each service has an "ownerRef" (team), "platformRef" (tech stack), and "zoneRef" (security zone)
- Services promote through environments following the promotion path

# Response Requirements

CRITICAL RULES:
✅ Answer ONLY using the context provided below - it contains the exact, authoritative information
✅ Use precise IDs, names, and counts directly from the context
✅ When listing services/resources, provide the actual names, not placeholders
✅ If the context lacks information, explicitly state: "The provided context doesn't include information about..."
✅ Be technical and specific - your audience consists of engineers and architects

RESPONSE QUALITY:
✅ Structure responses with markdown headings and lists for clarity
✅ Provide context and explanation, not just raw data
✅ For architectural summaries, highlight key patterns, dependencies, and insights
✅ For specific queries, include relevant details (owners, platforms, dependencies)
✅ Use professional, direct language without unnecessary pleasantries

SOURCE CITATIONS:
✅ **MANDATORY**: Every response MUST end with a "## Sources" section listing all referenced materials
✅ Extract the URL/path from each "**Source:**" line in the context and create a markdown link
✅ Format each source as: `- [descriptive-name](url-from-source-line)`
   - Example: If context shows "**Source:** /docs/flightplan/architecture-overview.md"
   - Output: `- [architecture-overview](/architecture-overview.md)`
✅ Use the filename (without .md extension) as the descriptive name
✅ If the same file appears multiple times, list it only once
✅ For compiled FlightPlan data (no Source line), use: `- [flightplan.compiled.json](flightplan.compiled.json)`
✅ Do NOT modify, shorten, or change the URLs - use them exactly as shown after "**Source:**"

FORBIDDEN:
❌ NEVER invent service names, resource names, or any data not in the context
❌ NEVER use placeholder examples like "service1, service2" - use actual names or say "not available"
❌ NEVER make assumptions about deployment, architecture, or technology choices
❌ NEVER add conversational filler like "I hope this helps!" or "Feel free to ask more!"

# Relevant Context:
{context}

# Analysis Guidelines:
When analyzing the context, consider relevant patterns in service interdependencies, team ownership, platform choices, security zones, resource sharing, and deployment strategy.
""";
    }

    private static async Task<string> QueryOllama(string baseUrl, string model, string systemPrompt, string userQuery, int? maxTokens)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        
        var request = new OllamaGenerateRequest
        {
            Model = model,
            Prompt = $"{systemPrompt}\n\n# User Question:\n{userQuery}\n\n# Answer:",
            Stream = false,
            Options = maxTokens.HasValue ? new OllamaOptions { NumPredict = maxTokens.Value } : null
        };

        var response = await client.PostAsJsonAsync($"{baseUrl}/api/generate", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
        return result?.Response ?? "No response received from Ollama";
    }

    private static async IAsyncEnumerable<string> QueryOllamaStreamingAsync(string baseUrl, string model, string systemPrompt, string userQuery, int? maxTokens)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        
        var request = new OllamaGenerateRequest
        {
            Model = model,
            Prompt = $"{systemPrompt}\n\n# User Question:\n{userQuery}\n\n# Answer:",
            Stream = true,
            Options = maxTokens.HasValue ? new OllamaOptions { NumPredict = maxTokens.Value } : null
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/generate")
        {
            Content = JsonContent.Create(request)
        };

        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var chunk = JsonSerializer.Deserialize<OllamaGenerateResponse>(line);
            if (chunk?.Response != null)
            {
                yield return chunk.Response;
            }

            if (chunk?.Done == true)
            {
                break;
            }
        }
    }

    // DTOs
    private class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("options")]
        public OllamaOptions? Options { get; set; }
    }

    private class OllamaOptions
    {
        [JsonPropertyName("num_predict")]
        public int NumPredict { get; set; }
    }

    private class OllamaGenerateResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("response")]
        public string? Response { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }
}

public class QueryResponse
{
    public bool Success { get; set; }
    public string? Answer { get; set; }
    public string? Error { get; set; }
    public int ChunksFound { get; set; }
    public double TopMatchScore { get; set; }
}
