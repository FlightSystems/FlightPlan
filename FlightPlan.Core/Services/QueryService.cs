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
    private readonly string _storeType;
    private readonly string? _connectionString;
    private readonly string? _indexName;

    public QueryService(
        string ollamaUrl = "http://localhost:11434",
        string model = "llama3.2",
        string embeddingModel = "nomic-embed-text",
        int topK = 5,
        string storeType = "json",
        string? connectionString = null,
        string? indexName = null)
    {
        _ollamaUrl = ollamaUrl;
        _model = model;
        _embeddingModel = embeddingModel;
        _topK = topK;
        _storeType = storeType;
        _connectionString = connectionString;
        _indexName = indexName;
    }

    public async Task<QueryResponse> QueryAsync(string query, int? maxTokens = null, string? baseUrlOverride = null)
    {
        var store = VectorStoreFactory.Create(_storeType, _connectionString, _indexName);
        
        if (!store.IndexExists())
        {
            return new QueryResponse
            {
                Success = false,
                Error = $"No index found for {_storeType} store. Please run 'flight index' first."
            };
        }

        try
        {
            // Get metadata for base URL (can be overridden for local serving)
            var metadata = await store.GetMetadata();
            var baseUrl = baseUrlOverride ?? metadata?.BaseUrl;
            
            // Search for relevant chunks
            var allChunks = await store.Search(query, _ollamaUrl, _embeddingModel, _topK);
            
            // Apply relevance filtering: only include chunks above similarity threshold
            // Thresholds based on empirical testing: 0.5+ = highly relevant, 0.3-0.5 = somewhat relevant, <0.3 = noise
            const double HIGH_RELEVANCE_THRESHOLD = 0.45;
            const double MIN_RELEVANCE_THRESHOLD = 0.25;
            
            var relevantChunks = allChunks.Where(c => c.Score >= HIGH_RELEVANCE_THRESHOLD).ToList();
            
            // If high-relevance filter is too strict (< 3 chunks), fall back to minimum threshold
            if (relevantChunks.Count < 3 && allChunks.Any())
            {
                relevantChunks = allChunks.Where(c => c.Score >= MIN_RELEVANCE_THRESHOLD).ToList();
                if (relevantChunks.Count > 0)
                {
                    Console.WriteLine($"⚠️ Relaxed relevance filter: {relevantChunks.Count} chunks pass minimum threshold (top score: {relevantChunks[0].Score:F3})");
                }
            }
            else if (relevantChunks.Count > 0)
            {
                var filtered = allChunks.Count - relevantChunks.Count;
                Console.WriteLine($"✅ Relevance filter: {relevantChunks.Count}/{allChunks.Count} chunks (filtered {filtered} low-relevance, top score: {relevantChunks[0].Score:F3})");
            }
            
            if (relevantChunks.Count == 0)
            {
                return new QueryResponse
                {
                    Success = false,
                    Error = $"No relevant context found for your query. Top match score: {(allChunks.Any() ? allChunks[0].Score.ToString("F3") : "N/A")} (threshold: {HIGH_RELEVANCE_THRESHOLD})"
                };
            }

            // Build context from chunks
            var context = BuildContext(relevantChunks, baseUrl);
            
            // If using FalkorDB, enrich context with graph data for relationship queries
            if (_storeType?.Equals("falkordb", StringComparison.OrdinalIgnoreCase) == true && store is FalkorDBVectorStore falkorStore)
            {
                var graphContext = await BuildGraphContext(query, relevantChunks, falkorStore);
                if (!string.IsNullOrEmpty(graphContext))
                {
                    context = graphContext + "\n\n" + context;
                }
            }
            
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
        var store = VectorStoreFactory.Create(_storeType, _connectionString, _indexName);
        
        if (!store.IndexExists())
        {
            yield return $"Error: No index found for {_storeType} store. Please run 'flight index' first.";
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
You are a precise FlightPlan architecture analyst providing VERIFIED, GROUNDED answers from authoritative sources.

# Information Sources (Priority Order)
1. **Graph Database Results** (marked with 🔼🔽 headers) - HIGHEST PRIORITY - verified relationships
2. **RAG Document Chunks** - architectural documentation with source citations
3. **Your Knowledge** - ONLY for explaining concepts, NEVER for facts about this system

# FlightPlan Knowledge
FlightPlan defines cloud-native architecture:
- **Services**: Microservices/APIs with IDs, owners (teams), platforms (tech), zones (security), exports (endpoints), dependencies
- **Resources**: Shared infrastructure (databases, queues, storage, caches)
- **Environments**: Deployment stages (dev → QA → UAT → prod)
- **Relationships**: DEPENDS_ON (service→resource/service), OWNED_BY (service→team), USES (service→resource)

# ACCURACY REQUIREMENTS (STRICTLY ENFORCE)

## Grounding Rules
✅ **EVERY FACT** must trace to provided context (graph results or RAG documents)
✅ **EXACT DATA**: Use precise IDs, names, counts from context—NO approximations
✅ **DISTINGUISH**: "Not found in context" ≠ "Doesn't exist"—be explicit
✅ **CONFLICTS**: If graph and documents disagree, report both with sources
✅ **VERIFICATION**: Graph data > Document data > General knowledge

## Response Quality
✅ **Direct Answer First**: Address the question immediately with verified facts
✅ **Graph Priority**: If graph results exist (🔼🔽 sections), cite them FIRST
✅ **Concrete Over Vague**: Replace "not explicitly listed", "we can infer", "might suggest" with definitive statements or explicit "unknown"
✅ **Evidence-Based**: Every claim needs a source— preferably multiple

## What to Do When Information is Missing
✅ State clearly: "The provided context does not include [specific information]"
✅ Suggest what additional queries/data would help
✅ If graph shows zero relationships, say: "Graph query returned zero results—either no relationships exist OR they're not documented"

## Forbidden Patterns
❌ "is not explicitly listed" → Instead: "✅ FOUND: [facts]" or "ℹ️ NOT IN CONTEXT: [what's missing]"
❌ "we can infer that..." → Only infer from EXPLICIT relationships
❌ "service1, service2, ..." → Real names OR "no matches found"
❌ "It's important to...", "Consider reviewing..." → No generic advice without context
❌ Speculation, hedging, or filler when facts are available
❌ Adding ANY information not in context

## Response Structure
1. **Answer** (with confidence level if graph data exists)
2. **Graph Evidence** (if 🔼🔽 sections present)
3. **Document Evidence** (RAG context)
4. **Limitations** (what context doesn't cover)
5. **Sources** (mandatory)

# Relevant Context:
{context}

# Analysis Focus:
Service interdependencies, team ownership, platform choices, security zones, resource sharing, deployment flows, failure impacts.
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

    private static async Task<string> BuildGraphContext(string query, List<FlightPlanChunk> relevantChunks, FalkorDBVectorStore falkorStore)
    {
        var sb = new StringBuilder();
        var queryLower = query.ToLowerInvariant();
        var enrichId = Guid.NewGuid().ToString("n").Substring(0, 8);
        
        try
        {
            Console.WriteLine($"🧭 [{enrichId}] Graph enrichment start");
            Console.WriteLine($"🧭 [{enrichId}] Query: {query}");
            Console.WriteLine($"🧭 [{enrichId}] Relevant chunks: {relevantChunks.Count}");

            // Extract service names from the query and relevant chunks
            var serviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var teamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            // Extract from chunks (services are often in the chunk ID)
            foreach (var chunk in relevantChunks.Take(3)) // Only top 3 chunks to avoid too much processing
            {
                if (chunk.Type == "Service" && !string.IsNullOrEmpty(chunk.Id))
                {
                    serviceNames.Add(chunk.Id);
                    Console.WriteLine($"🔍 [{enrichId}] Extracted service from chunk: {chunk.Id}");
                }
                else if (chunk.Type == "Team" && !string.IsNullOrEmpty(chunk.Id))
                {
                    teamNames.Add(chunk.Id);
                    Console.WriteLine($"🔍 [{enrichId}] Extracted team from chunk: {chunk.Id}");
                }
                else
                {
                    Console.WriteLine($"🧩 [{enrichId}] Top chunk: type={chunk.Type}, id={chunk.Id}");
                }
            }
            
            // Also try to extract service names directly from the query using common patterns
            // Patterns: "depend on X", "uses X", "X service", "X API", etc.
            var patterns = new[]
            {
                // "what does X depend on" or "does X depend on"
                @"\b(?:what\s+does|does)\s+(?:the\s+)?([a-zA-Z][a-zA-Z0-9\s\-_/]+?)\s+depend\s+on\b",
                // "what depends on X" / "depends on X" / "depend on X"
                @"\bdepend[s]?\s+on\s+(?:the\s+)?([a-zA-Z][a-zA-Z0-9\s\-_/]+?)(?:\s+(?:service|api|component|processor))?(?:\?|$|,|\s+and|\s+or)",
                // "uses X" / "use X"
                @"\buses?\s+(?:the\s+)?([a-zA-Z][a-zA-Z0-9\s\-_/]+?)(?:\s+(?:service|api|component|processor))?(?:\?|$|,|\s+and|\s+or)",
                // "dependencies of X"
                @"\bdependencies\s+of\s+(?:the\s+)?([a-zA-Z][a-zA-Z0-9\s\-_/]+?)(?:\s+(?:service|api|component|processor))?(?:\?|$|,|\s+and|\s+or)",
                // "X service" / "X api" / etc.
                @"\b([a-zA-Z][a-zA-Z0-9\s\-_/]+?)\s+(?:service|api|component|processor)(?:\s+depends|\s+uses|\s+fails|\s+breaks)?\b",
                // team ownership
                @"\b(?:team\s+)?([a-zA-Z][a-zA-Z0-9\s\-_]+?)\s+(?:owns?|responsible)\b",
            };
            
            var questionWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
            { 
                "what", "which", "who", "where", "when", "why", "how", "does", "is", "are", "can", "could", "would", "should"
            };
            
            foreach (var pattern in patterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(query, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var extractedName = match.Groups[1].Value.Trim();
                        // Clean up common words
                        extractedName = System.Text.RegularExpressions.Regex.Replace(extractedName, @"\s+(service|api|component|processor)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                        extractedName = System.Text.RegularExpressions.Regex.Replace(extractedName, @"^[\s\p{P}]+|[\s\p{P}]+$", "").Trim();
                        
                        // Filter out names that start with question words
                        var startsWithQuestionWord = questionWords.Any(qw => 
                            extractedName.StartsWith(qw + " ", StringComparison.OrdinalIgnoreCase) ||
                            extractedName.Equals(qw, StringComparison.OrdinalIgnoreCase));
                        
                        // Filter out question words and very short names
                        if (!string.IsNullOrWhiteSpace(extractedName) && 
                            extractedName.Length > 2 && 
                            !startsWithQuestionWord)
                        {
                            serviceNames.Add(extractedName);
                            Console.WriteLine($"🔍 [{enrichId}] Extracted service from query pattern: {extractedName} (pattern={pattern})");
                        }
                        else if (extractedName.Length > 2 && startsWithQuestionWord)
                        {
                            // Try to strip question words from the beginning
                            var cleaned = extractedName;
                            foreach (var qw in questionWords.OrderByDescending(w => w.Length))
                            {
                                if (cleaned.StartsWith(qw + " ", StringComparison.OrdinalIgnoreCase))
                                {
                                    cleaned = cleaned.Substring(qw.Length + 1).Trim();
                                    // Recursively clean any remaining question words
                                    while (questionWords.Any(w => cleaned.StartsWith(w + " ", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        var nextQw = questionWords.FirstOrDefault(w => cleaned.StartsWith(w + " ", StringComparison.OrdinalIgnoreCase));
                                        if (nextQw != null)
                                        {
                                            cleaned = cleaned.Substring(nextQw.Length + 1).Trim();
                                        }
                                    }
                                    break;
                                }
                            }
                            
                            if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Length > 2 && cleaned != extractedName)
                            {
                                serviceNames.Add(cleaned);
                                Console.WriteLine($"🔍 [{enrichId}] Extracted service from query pattern (cleaned): {cleaned} (pattern={pattern})");
                            }
                        }
                    }
                }
            }

            // If we still didn't find any services, attempt a resolution-based fallback using significant query terms.
            // This helps for lowercase queries ("adjudication") and non-pattern queries ("tell me about adjudication processor").
            if (!serviceNames.Any())
            {
                Console.WriteLine($"🧪 [{enrichId}] No services extracted from chunks/patterns; attempting ResolveServiceIds fallback");

                var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "what","which","who","where","when","why","how","does","do","did","is","are","can","could","would","should",
                    "tell","explain","show","list","give","me","about","the","a","an","to","of","for","on","in","with","and","or",
                    "service","services","api","component","processor","dependency","dependencies","depend","depends","use","uses","using","impact","fail","fails","break","breaks","affect","affects"
                };

                var normalized = System.Text.RegularExpressions.Regex.Replace(queryLower, @"[^a-z0-9\s\-_/]", " ");
                var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim())
                    .Where(t => t.Length > 3 && !stopWords.Contains(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList();

                Console.WriteLine($"🧪 [{enrichId}] Fallback tokens: {string.Join(", ", tokens)}");

                foreach (var token in tokens)
                {
                    var resolved = await falkorStore.ResolveServiceIds(token);
                    Console.WriteLine($"🧪 [{enrichId}] ResolveServiceIds('{token}') -> {resolved.Count} matches{(resolved.Any() ? ": " + string.Join(", ", resolved) : string.Empty)}");
                    foreach (var id in resolved)
                    {
                        serviceNames.Add(id);
                    }

                    if (serviceNames.Count >= 3)
                    {
                        break;
                    }
                }
            }
            
            Console.WriteLine($"📊 [{enrichId}] Graph enrichment: Found {serviceNames.Count} services, {teamNames.Count} teams");
            if (serviceNames.Any())
            {
                Console.WriteLine($"   [{enrichId}] Services: {string.Join(", ", serviceNames)}");
            }
            if (teamNames.Any())
            {
                Console.WriteLine($"   [{enrichId}] Teams: {string.Join(", ", teamNames)}");
            }
            
            // Detect query intent and run appropriate graph queries
            var addedGraphData = false;
            
            // Determine dependency direction from query structure
            // "What services depend on X" or "What depends on X" → find dependents (things that use X)
            // "What does X depend on" or "X dependencies" → find dependencies (things X uses)
            
            // Priority 1: Check for "what does X depend on" pattern FIRST (dependency query) 
            var isDependencyQuery = serviceNames.Any() &&
                                   (System.Text.RegularExpressions.Regex.IsMatch(queryLower, @"\b(?:what\s+does|does)\s+.+\s+depend\s+on\b") ||
                                    queryLower.Contains("dependencies of") ||
                                    System.Text.RegularExpressions.Regex.IsMatch(queryLower, @"\b(?:what\s+does|does)\s+.+\s+require\b"));
                                   
            // Priority 2: Check for "what depends on X" pattern (dependent query) only if NOT a dependency query
            var isDependentQuery = !isDependencyQuery && 
                                   (queryLower.Contains("depend on") || queryLower.Contains("depends on") || 
                                   queryLower.Contains("use the") || queryLower.Contains("uses the")) &&
                                   serviceNames.Any();

            Console.WriteLine($"🧠 [{enrichId}] Intent: isDependencyQuery={isDependencyQuery}, isDependentQuery={isDependentQuery}");
            
            // Dependent queries (what depends ON this service)
            if (isDependentQuery)
            {
                Console.WriteLine($"🔼 [{enrichId}] Running DEPENDENT queries (what uses these services)");
                sb.AppendLine("## 🔼 Verified Dependencies from Graph Database\n");
                sb.AppendLine("**Data Source**: Live graph database queries (HIGHEST CONFIDENCE)\n");
                
                var foundAny = false;
                foreach (var serviceName in serviceNames.Take(3)) // Limit to 3 for performance
                {
                    Console.WriteLine($"   [{enrichId}] Querying dependents of: {serviceName}");
                    var dependents = await falkorStore.GetServiceDependents(serviceName);
                    Console.WriteLine($"   [{enrichId}] Found {dependents.Count} dependents");
                    
                    if (dependents.Any())
                    {
                        foundAny = true;
                        sb.AppendLine($"### ✅ Services depending on **{serviceName}** ({dependents.Count} found):\n");
                        foreach (var dep in dependents)
                        {
                            var name = dep.Properties.GetValueOrDefault("name", dep.Id)?.ToString() ?? dep.Id;
                            var desc = dep.Properties.GetValueOrDefault("description")?.ToString();
                            sb.AppendLine($"- **{name}**");
                            sb.AppendLine($"  - ID: `{dep.Id}`");
                            if (!string.IsNullOrWhiteSpace(desc))
                            {
                                sb.AppendLine($"  - Description: {desc}");
                            }
                        }
                        sb.AppendLine();
                        addedGraphData = true;
                    }
                    else
                    {
                        sb.AppendLine($"### ℹ️ **{serviceName}**: Zero dependencies found\n");
                        sb.AppendLine($"This means:");
                        sb.AppendLine($"1. No other services currently depend on this service, OR");
                        sb.AppendLine($"2. This is a leaf node in the dependency graph, OR");
                        sb.AppendLine($"3. Dependencies may not be fully documented in graph\n");
                    }
                }
                
                if (foundAny)
                {
                    sb.AppendLine("---\n");
                    sb.AppendLine("**Confidence Level**: ⭐⭐⭐⭐⭐ VERY HIGH - Direct graph query\n");
                }
            }
            
            // Dependency queries (what this service depends on)
            if (isDependencyQuery && !isDependentQuery)
            {
                Console.WriteLine($"🔗 [{enrichId}] Running DEPENDENCY queries (what these services use)");
                sb.AppendLine("## 🔗 Verified Service Dependencies from Graph Database\n");
                sb.AppendLine("**Data Source**: Live graph database queries (HIGHEST CONFIDENCE)\n");
                
                var foundAny = false;
                foreach (var serviceName in serviceNames.Take(3)) // Limit to 3 for performance
                {
                    Console.WriteLine($"   [{enrichId}] Querying dependencies of: {serviceName}");
                    var dependencies = await falkorStore.GetServiceDependencies(serviceName);
                    Console.WriteLine($"   [{enrichId}] Found {dependencies.Count} dependencies");
                    
                    if (dependencies.Any())
                    {
                        foundAny = true;
                        sb.AppendLine($"### ✅ **{serviceName}** depends on ({dependencies.Count} found):\n");
                        foreach (var dep in dependencies)
                        {
                            var name = dep.Properties.GetValueOrDefault("name", dep.Id)?.ToString() ?? dep.Id;
                            var desc = dep.Properties.GetValueOrDefault("description")?.ToString();
                            sb.AppendLine($"- **{name}**");
                            sb.AppendLine($"  - ID: `{dep.Id}`");
                            if (!string.IsNullOrWhiteSpace(desc))
                            {
                                sb.AppendLine($"  - Description: {desc}");
                            }
                        }
                        sb.AppendLine();
                        addedGraphData = true;
                    }
                    else
                    {
                        sb.AppendLine($"### ℹ️ **{serviceName}**: Zero dependencies found\n");
                        sb.AppendLine($"This service either:");
                        sb.AppendLine($"1. Has no documented dependencies in the graph, OR");
                        sb.AppendLine($"2. Operates independently without external service dependencies, OR");
                        sb.AppendLine($"3. Service name did not resolve to a graph node\n");
                    }
                }
                
                if (foundAny)
                {
                    sb.AppendLine("---\n");
                    sb.AppendLine("**Confidence Level**: ⭐⭐⭐⭐⭐ VERY HIGH - Direct graph query\n");
                }
            }
            
            // Impact analysis
            if ((queryLower.Contains("impact") || queryLower.Contains("fail") || queryLower.Contains("break") || queryLower.Contains("affect")) && serviceNames.Any())
            {
                Console.WriteLine($"💥 [{enrichId}] Running IMPACT ANALYSIS queries");
                sb.AppendLine("## 💥 Impact Analysis (from Graph Database)\n");
                foreach (var serviceName in serviceNames.Take(2))
                {
                    Console.WriteLine($"   [{enrichId}] Analyzing impact of: {serviceName}");
                    var impact = await falkorStore.GetImpactAnalysis(serviceName);
                    Console.WriteLine($"   [{enrichId}] Found {impact.Count} impacted services");
                    
                    if (impact.Any())
                    {
                        sb.AppendLine($"If **{serviceName}** fails, it would impact:");
                        foreach (var node in impact)
                        {
                            sb.AppendLine($"- {node.Id} ({node.Label})");
                        }
                        sb.AppendLine();
                        addedGraphData = true;
                    }
                    else
                    {
                        sb.AppendLine($"No services would be directly impacted if **{serviceName}** fails.\n");
                    }
                }
            }
            
            // Ownership queries
            if ((queryLower.Contains("own") || queryLower.Contains("team") || queryLower.Contains("responsible")) && (serviceNames.Any() || teamNames.Any()))
            {
                Console.WriteLine($"👥 [{enrichId}] Running OWNERSHIP queries");
                sb.AppendLine("## 👥 Team Ownership (from Graph Database)\n");
                foreach (var teamName in teamNames.Take(2))
                {
                    Console.WriteLine($"   [{enrichId}] Querying ownership for team: {teamName}");
                    var ownership = await falkorStore.GetTeamOwnership(teamName);
                    Console.WriteLine($"   [{enrichId}] Found {ownership.Count} ownership categories");
                    
                    if (ownership.Any())
                    {
                        sb.AppendLine($"Team **{teamName}** owns:");
                        var allNodes = ownership.Values.SelectMany(nodes => nodes).ToList();
                        foreach (var node in allNodes.Where(n => n.Label == "Service"))
                        {
                            sb.AppendLine($"- {node.Id} (Service)");
                        }
                        foreach (var node in allNodes.Where(n => n.Label == "Resource"))
                        {
                            sb.AppendLine($"- {node.Id} (Resource)");
                        }
                        sb.AppendLine();
                        addedGraphData = true;
                    }
                    else
                    {
                        sb.AppendLine($"No ownership data found for team **{teamName}**.\n");
                    }
                }
            }
            
            if (addedGraphData)
            {
                sb.AppendLine("---\n");
                return sb.ToString();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️  [{enrichId}] Graph context enrichment failed: {ex.Message}");
        }
        
        return string.Empty;
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
