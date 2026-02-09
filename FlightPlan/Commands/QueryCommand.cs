using System.CommandLine;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlightPlan.Commands;

internal static class QueryCommand
{
    internal static void Configure(
        Command queryCmd,
        Option<FileInfo?> compiledJsonOpt,
        Argument<string> queryArg,
        Option<string> modelOpt,
        Option<string> ollamaUrlOpt,
        Option<bool> streamOpt,
        Option<int?> maxTokensOpt,
        Option<string> embeddingModelOpt,
        Option<int> topKOpt)
    {
        queryCmd.SetHandler(async (FileInfo? compiledJson, string query, string model, string ollamaUrl, bool stream, int? maxTokens, string embeddingModel, int topK) =>
        {
            // If no file provided, try to get from index or auto-discover
            FileInfo? resolvedFile = compiledJson;
            
            if (resolvedFile == null)
            {
                resolvedFile = await TryResolveFlightPlanFile();
                if (resolvedFile == null)
                {
                    Console.Error.WriteLine("❌ No FlightPlan file specified and couldn't auto-discover one.");
                    Console.Error.WriteLine("\nOptions:");
                    Console.Error.WriteLine("  1. Specify the file: flight query <path-to-json> \"your question\"");
                    Console.Error.WriteLine("  2. Create flightplan.compiled.json in current directory");
                    Console.Error.WriteLine("  3. Index a file first: flight index <path-to-json>");
                    Environment.Exit(1);
                }
                Console.WriteLine($"📄 Using FlightPlan: {Path.GetFileName(resolvedFile.FullName)}");
            }
            else
            {
                CliHelpers.EnsureFileExists(resolvedFile, "Compiled FlightPlan JSON file");
            }

            // Check if index exists
            var store = new VectorStore();
            if (!store.IndexExists())
            {
                Console.Error.WriteLine("❌ No index found. Please run 'flight index' first:");
                Console.Error.WriteLine($"   flight index {resolvedFile.Name}");
                Environment.Exit(1);
            }

            Console.WriteLine($"🔍 Searching for relevant context...");
            
            try
            {
                // Get metadata for base URL
                var metadata = await store.GetMetadata();
                var baseUrl = metadata?.BaseUrl;
                
                // Search for relevant chunks
                var relevantChunks = await store.Search(query, ollamaUrl, embeddingModel, topK);
                
                Console.WriteLine($"   Found {relevantChunks.Count} relevant chunks");
                Console.WriteLine($"   Top match: {relevantChunks[0].Id} (score: {relevantChunks[0].Score:F3})\n");

                // Build context from chunks
                var context = BuildContext(relevantChunks, baseUrl);
                
                // Build the system prompt with relevant context only
                var systemPrompt = BuildSystemPrompt(context);

                // Send query to Ollama
                Console.WriteLine($"🤖 Querying {model} via Ollama...\n");
                
                if (stream)
                {
                    await QueryOllamaStreaming(ollamaUrl, model, systemPrompt, query, maxTokens);
                }
                else
                {
                    var response = await QueryOllama(ollamaUrl, model, systemPrompt, query, maxTokens);
                    Console.WriteLine(response);
                }
                
                Console.WriteLine("\n\n✔ Query completed successfully.");
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine($"❌ {ex.Message}");
                Environment.Exit(1);
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"❌ Failed to connect to Ollama at {ollamaUrl}");
                Console.Error.WriteLine($"   Error: {ex.Message}");
                Console.Error.WriteLine($"\n   Make sure Ollama is running: ollama serve");
                Console.Error.WriteLine($"   And the model is available: ollama pull {model}");
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"❌ Query failed: {ex.Message}");
                Environment.Exit(1);
            }

        }, compiledJsonOpt, queryArg, modelOpt, ollamaUrlOpt, streamOpt, maxTokensOpt, embeddingModelOpt, topKOpt);
    }

    private static async Task<FileInfo?> TryResolveFlightPlanFile()
    {
        // 1. Try to get from index metadata
        var store = new VectorStore();
        if (store.IndexExists())
        {
            var metadata = await store.GetMetadata();
            if (metadata != null && File.Exists(metadata.SourceFile))
            {
                return new FileInfo(metadata.SourceFile);
            }
        }

        // 2. Look for flightplan.compiled.json in current directory
        var currentDir = Directory.GetCurrentDirectory();
        var commonNames = new[] 
        { 
            "flightplan.compiled.json",
            "docs/flightplan/flightplan.compiled.json"
        };

        foreach (var name in commonNames)
        {
            var path = Path.Combine(currentDir, name);
            if (File.Exists(path))
            {
                return new FileInfo(path);
            }
        }

        return null;
    }

    private static string BuildContext(List<FlightPlanChunk> chunks, string? baseUrl)
    {
        var sb = new StringBuilder();
        
        foreach (var chunk in chunks)
        {
            sb.AppendLine($"## {chunk.Type}: {chunk.Id}");
            
            // Add source citation if available
            if (!string.IsNullOrEmpty(chunk.SourceFile))
            {
                // Construct full URL if baseUrl is provided
                var sourceReference = !string.IsNullOrEmpty(baseUrl) 
                    ? $"{baseUrl.TrimEnd('/')}/{chunk.SourceFile}"
                    : chunk.SourceFile;
                    
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
   - Output: `- [architecture-overview](/docs/flightplan/architecture-overview.md)`
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
When analyzing the context, consider:
- Service interdependencies and coupling
- Team ownership distribution and boundaries
- Platform diversity and technology choices
- Security zone isolation patterns
- Resource sharing and potential bottlenecks
- Environment promotion paths and deployment strategy
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

    private static async Task QueryOllamaStreaming(string baseUrl, string model, string systemPrompt, string userQuery, int? maxTokens)
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

            try
            {
                var chunk = JsonSerializer.Deserialize<OllamaGenerateResponse>(line);
                if (chunk?.Response != null)
                {
                    Console.Write(chunk.Response);
                }

                if (chunk?.Done == true)
                {
                    break;
                }
            }
            catch (JsonException)
            {
                // Skip malformed lines
                continue;
            }
        }
    }

    // Ollama API models
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

        [JsonPropertyName("context")]
        public int[]? Context { get; set; }

        [JsonPropertyName("total_duration")]
        public long? TotalDuration { get; set; }

        [JsonPropertyName("load_duration")]
        public long? LoadDuration { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public int? PromptEvalCount { get; set; }

        [JsonPropertyName("eval_count")]
        public int? EvalCount { get; set; }

        [JsonPropertyName("eval_duration")]
        public long? EvalDuration { get; set; }
    }
}
