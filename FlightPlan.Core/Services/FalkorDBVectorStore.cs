using System.Text.Json;
using FlightPlan.Models;
using StackExchange.Redis;

namespace FlightPlan.Services;

/// <summary>
/// FalkorDB-based vector store with graph and vector search capabilities
/// Uses FalkorDB (Redis-based graph database) for storing chunks with vector embeddings
/// </summary>
public class FalkorDBVectorStore : IVectorStore
{
    private readonly string _connectionString;
    private readonly string _graphName;
    private IDatabase? _db;
    private ConnectionMultiplexer? _redis;
    private string MetadataKey => $"{_graphName}:metadata";
    private string ChunkKeyPrefix => $"{_graphName}:chunk:";

    public string StoreType => "falkordb";

    public FalkorDBVectorStore(string connectionString = "localhost:6379", string graphName = "flightplan")
    {
        _connectionString = connectionString;
        _graphName = graphName;
    }

    private async Task EnsureConnection()
    {
        if (_redis == null || !_redis.IsConnected)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(_connectionString);
            _db = _redis.GetDatabase();
        }
    }

    public async Task<int> IndexFlightPlan(
        FileInfo compiledJson, 
        string? docsDirectory, 
        string? baseUrl, 
        string ollamaUrl = "http://localhost:11434", 
        string embeddingModel = "nomic-embed-text")
    {
        await EnsureConnection();
        
        Console.WriteLine("📖 Loading FlightPlan...");
        var jsonContent = await File.ReadAllTextAsync(compiledJson.FullName);
        var doc = JsonDocument.Parse(jsonContent);

        // Store metadata
        var metadata = new VectorStoreMetadata
        {
            SourceFile = compiledJson.FullName,
            IndexedAt = DateTime.UtcNow,
            EmbeddingModel = embeddingModel,
            ChunkCount = 0,
            IncludesDocumentation = !string.IsNullOrEmpty(docsDirectory),
            BaseUrl = baseUrl,
            GraphName = _graphName
        };

        Console.WriteLine("✂️  Chunking FlightPlan...");
        var chunker = new FlightPlanChunker();
        var chunks = chunker.ChunkFlightPlan(doc);
        Console.WriteLine($"   Created {chunks.Count} FlightPlan chunks");

        // Add documentation chunks if directory provided
        if (!string.IsNullOrEmpty(docsDirectory))
        {
            Console.WriteLine($"📚 Chunking documentation from {docsDirectory}...");
            var markdownChunker = new MarkdownChunker();
            var docChunks = markdownChunker.ChunkMarkdownFiles(docsDirectory);
            chunks.AddRange(docChunks);
            Console.WriteLine($"   Created {docChunks.Count} documentation chunks");
        }

        metadata.ChunkCount = chunks.Count;
        Console.WriteLine($"   Total: {chunks.Count} chunks");

        Console.WriteLine("🧠 Generating embeddings...");
        var embedder = new EmbeddingService(ollamaUrl, embeddingModel);
        
        var progress = 0;
        var batchSize = 3;
        var delayMs = 250;
        
        // Clear existing index
        Console.WriteLine("🧹 Clearing existing FalkorDB index...");
        await DeleteIndexInternal();
        
        for (var i = 0; i < chunks.Count; i++)
        {
            try
            {
                chunks[i].Embedding = await embedder.GenerateEmbedding(chunks[i].Content);
                
                // Store chunk in Redis with vector
                await StoreChunk(chunks[i]);
                
                progress++;
                
                if (progress % 10 == 0)
                {
                    Console.WriteLine($"   Embedded and stored {progress}/{chunks.Count} chunks...");
                }
                
                if (progress % batchSize == 0)
                {
                    await Task.Delay(delayMs);
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.InternalServerError)
            {
                Console.WriteLine($"\n⚠️  Ollama error at chunk {progress}/{chunks.Count}. Waiting 5s and retrying...");
                await Task.Delay(5000);
                
                try
                {
                    chunks[i].Embedding = await embedder.GenerateEmbedding(chunks[i].Content);
                    await StoreChunk(chunks[i]);
                    progress++;
                    Console.WriteLine($"   ✓ Retry successful, continuing...");
                }
                catch (Exception retryEx)
                {
                    Console.WriteLine($"\n⚠️  Skipping problematic chunk {progress}:");
                    Console.WriteLine($"   ID: {chunks[i].Id}");
                    Console.WriteLine($"   Error: {retryEx.Message}");
                    chunks.RemoveAt(i);
                    i--;
                }
            }
        }

        Console.WriteLine("💾 Saving metadata to FalkorDB...");
        await StoreMetadata(metadata);
        
        Console.WriteLine("🔗 Creating graph relationships...");
        await CreateGraphRelationships(chunks, doc);
        
        return chunks.Count;
    }

    private async Task StoreChunk(FlightPlanChunk chunk)
    {
        if (_db == null) throw new InvalidOperationException("Database not connected");
        
        var chunkKey = $"{ChunkKeyPrefix}{chunk.Id}";
        var chunkData = new Dictionary<string, RedisValue>
        {
            ["id"] = chunk.Id,
            ["type"] = chunk.Type,
            ["content"] = chunk.Content,
            ["sourceFile"] = chunk.SourceFile ?? "",
            ["sourceSection"] = chunk.SourceSection ?? "",
            ["embedding"] = JsonSerializer.Serialize(chunk.Embedding),
            ["metadata"] = JsonSerializer.Serialize(chunk.Metadata)
        };
        
        await _db.HashSetAsync(chunkKey, chunkData.Select(kvp => new HashEntry(kvp.Key, kvp.Value)).ToArray());
    }

    private async Task StoreMetadata(VectorStoreMetadata metadata)
    {
        if (_db == null) throw new InvalidOperationException("Database not connected");
        
        var metadataJson = JsonSerializer.Serialize(metadata);
        await _db.StringSetAsync(MetadataKey, metadataJson);
    }

    private async Task CreateGraphRelationships(List<FlightPlanChunk> chunks, JsonDocument doc)
    {
        // Create graph relationships in FalkorDB using Cypher queries
        // This creates actual graph nodes and edges that can be visualized
        
        try
        {
            if (_db == null) throw new InvalidOperationException("Database not connected");
            
            var root = doc.RootElement;
            var serviceNames = new HashSet<string>();
            var nodeCount = 0;
            var relationshipCount = 0;
            
            // Detect schema version and extract services accordingly
            if (root.TryGetProperty("services", out var services))
            {
                // Original schema: services at root level
                (nodeCount, relationshipCount) = await CreateGraphFromOriginalSchema(services, root);
            }
            else if (root.TryGetProperty("dependencies", out var dependencies))
            {
                // New schema: services in dependencies and interfaces
                (nodeCount, relationshipCount) = await CreateGraphFromDependenciesSchema(root, dependencies);
            }
            
            Console.WriteLine($"   ✓ Created {nodeCount} nodes and {relationshipCount} relationships");
            if (nodeCount > 0)
            {
                Console.WriteLine($"   📊 Graph ready! Query with: redis-cli --raw GRAPH.QUERY {_graphName} \"MATCH (n) RETURN n LIMIT 25\"");
            }
            else
            {
                Console.WriteLine($"   ⚠️  No services found to create graph");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ⚠️ Warning: Could not create all graph relationships: {ex.Message}");
            Console.WriteLine($"   Graph features may be limited, but vector search will still work.");
        }
    }

    private async Task<(int nodes, int relationships)> CreateGraphFromOriginalSchema(JsonElement services, JsonElement root)
    {
        var nodeCount = 0;
        var relationshipCount = 0;
        
        // Create Service nodes
        foreach (var service in services.EnumerateArray())
        {
            if (service.TryGetProperty("name", out var serviceName))
            {
                var name = serviceName.GetString();
                var description = service.TryGetProperty("description", out var desc) ? desc.GetString() : "";
                var owner = service.TryGetProperty("owner", out var own) ? own.GetString() : "";
                
                var createNodeQuery = $"CREATE (s:Service {{id: '{EscapeCypher(name)}', name: '{EscapeCypher(name)}', description: '{EscapeCypher(description)}', owner: '{EscapeCypher(owner)}'}})";
                await ExecuteGraphQuery(createNodeQuery);
                nodeCount++;
            }
        }
        
        // Create Team nodes
        if (root.TryGetProperty("teams", out var teams))
        {
            foreach (var team in teams.EnumerateArray())
            {
                if (team.TryGetProperty("name", out var teamName))
                {
                    var name = teamName.GetString();
                    await ExecuteGraphQuery($"CREATE (t:Team {{id: '{EscapeCypher(name)}', name: '{EscapeCypher(name)}'}})");
                    nodeCount++;
                }
            }
        }
        
        // Create Environment nodes
        if (root.TryGetProperty("environments", out var environments))
        {
            foreach (var env in environments.EnumerateArray())
            {
                if (env.TryGetProperty("name", out var envName))
                {
                    var name = envName.GetString();
                    await ExecuteGraphQuery($"CREATE (e:Environment {{id: '{EscapeCypher(name)}', name: '{EscapeCypher(name)}'}})");
                    nodeCount++;
                }
            }
        }
        
        // Create relationships
        foreach (var service in services.EnumerateArray())
        {
            if (service.TryGetProperty("name", out var serviceName))
            {
                var sourceService = serviceName.GetString();
                
                // DEPENDS_ON relationships
                if (service.TryGetProperty("dependencies", out var deps))
                {
                    foreach (var dep in deps.EnumerateArray())
                    {
                        if (dep.TryGetProperty("service", out var depServiceName))
                        {
                            var targetService = depServiceName.GetString();
                            await ExecuteGraphQuery($"MATCH (a:Service {{id: '{EscapeCypher(sourceService)}'}}), (b:Service {{id: '{EscapeCypher(targetService)}'}}) CREATE (a)-[:DEPENDS_ON]->(b)");
                            relationshipCount++;
                        }
                    }
                }
                
                // OWNED_BY relationships
                if (service.TryGetProperty("owner", out var owner))
                {
                    var ownerName = owner.GetString();
                    await ExecuteGraphQuery($"MATCH (s:Service {{id: '{EscapeCypher(sourceService)}'}}), (t:Team {{id: '{EscapeCypher(ownerName)}'}}) CREATE (s)-[:OWNED_BY]->(t)");
                    relationshipCount++;
                }
            }
        }
        
        return (nodeCount, relationshipCount);
    }

    private async Task<(int nodes, int relationships)> CreateGraphFromDependenciesSchema(JsonElement root, JsonElement dependencies)
    {
        var nodeCount = 0;
        var relationshipCount = 0;
        
        if (!root.TryGetProperty("entities", out var entities))
        {
            Console.WriteLine("   ⚠️  No entities found in FlightPlan");
            return (0, 0);
        }
        
        // Create Service nodes with full metadata from entities.services
        if (entities.TryGetProperty("services", out var services))
        {
            foreach (var service in services.EnumerateArray())
            {
                if (!service.TryGetProperty("id", out var serviceId))
                    continue;
                    
                var id = serviceId.GetString();
                var name = service.TryGetProperty("name", out var n) ? n.GetString() : id;
                var description = service.TryGetProperty("description", out var d) ? EscapeCypher(d.GetString()) : "";
                var owner = service.TryGetProperty("ownerRef", out var o) ? EscapeCypher(o.GetString()) : "";
                var platform = service.TryGetProperty("platformRef", out var p) ? EscapeCypher(p.GetString()) : "";
                var zone = service.TryGetProperty("zoneRef", out var z) ? EscapeCypher(z.GetString()) : "";
                var repoUrl = service.TryGetProperty("repoUrl", out var r) ? EscapeCypher(r.GetString()) : "";
                var type = service.TryGetProperty("type", out var t) ? EscapeCypher(t.GetString()) : "service";
                
                var query = $"CREATE (s:Service {{" +
                    $"id: '{EscapeCypher(id)}', " +
                    $"name: '{EscapeCypher(name)}', " +
                    $"description: '{description}', " +
                    $"type: '{type}', " +
                    $"owner: '{owner}', " +
                    $"platform: '{platform}', " +
                    $"zone: '{zone}', " +
                    $"repoUrl: '{repoUrl}'" +
                    $"}})";
                    
                await ExecuteGraphQuery(query);
                nodeCount++;
            }
        }
        
        // Create Team nodes with full metadata
        if (entities.TryGetProperty("teams", out var teams))
        {
            foreach (var team in teams.EnumerateArray())
            {
                if (!team.TryGetProperty("id", out var teamId))
                    continue;
                    
                var id = teamId.GetString();
                var name = team.TryGetProperty("name", out var n) ? n.GetString() : id;
                var description = team.TryGetProperty("description", out var d) ? EscapeCypher(d.GetString()) : "";
                var type = team.TryGetProperty("type", out var t) ? EscapeCypher(t.GetString()) : "team";
                
                var query = $"CREATE (t:Team {{" +
                    $"id: '{EscapeCypher(id)}', " +
                    $"caption: '{EscapeCypher(name)}', " +
                    $"title: '{EscapeCypher(name)}', " +
                    $"name: '{EscapeCypher(name)}', " +
                    $"label: '{EscapeCypher(name)}', " +
                    $"description: '{description}', " +
                    $"type: '{type}'" +
                    $"}})";
                    
                await ExecuteGraphQuery(query);
                nodeCount++;
            }
        }
        
        // Create Resource nodes with full metadata
        if (entities.TryGetProperty("resources", out var resources))
        {
            foreach (var resource in resources.EnumerateArray())
            {
                if (!resource.TryGetProperty("id", out var resourceId))
                    continue;
                    
                var id = resourceId.GetString();
                var name = resource.TryGetProperty("name", out var n) ? n.GetString() : id;
                var description = resource.TryGetProperty("description", out var d) ? EscapeCypher(d.GetString()) : "";
                var kind = resource.TryGetProperty("kind", out var k) ? EscapeCypher(k.GetString()) : "";
                var owner = resource.TryGetProperty("ownerRef", out var o) ? EscapeCypher(o.GetString()) : "";
                var platform = resource.TryGetProperty("platformRef", out var p) ? EscapeCypher(p.GetString()) : "";
                var zone = resource.TryGetProperty("zoneRef", out var z) ? EscapeCypher(z.GetString()) : "";
                var type = resource.TryGetProperty("type", out var t) ? EscapeCypher(t.GetString()) : "resource";
                
                var query = $"CREATE (r:Resource {{" +
                    $"id: '{EscapeCypher(id)}', " +
                    $"caption: '{EscapeCypher(name)}', " +
                    $"title: '{EscapeCypher(name)}', " +
                    $"name: '{EscapeCypher(name)}', " +
                    $"label: '{EscapeCypher(name)}', " +
                    $"description: '{description}', " +
                    $"type: '{type}', " +
                    $"kind: '{kind}', " +
                    $"owner: '{owner}', " +
                    $"platform: '{platform}', " +
                    $"zone: '{zone}'" +
                    $"}})";
                    
                await ExecuteGraphQuery(query);
                nodeCount++;
            }
        }
        
        // Create Environment nodes with full metadata
        if (entities.TryGetProperty("environments", out var environments))
        {
            foreach (var env in environments.EnumerateArray())
            {
                if (!env.TryGetProperty("id", out var envId))
                    continue;
                    
                var id = envId.GetString();
                var name = env.TryGetProperty("name", out var n) ? n.GetString() : id;
                var description = env.TryGetProperty("description", out var d) ? EscapeCypher(d.GetString()) : "";
                var type = env.TryGetProperty("type", out var t) ? EscapeCypher(t.GetString()) : "environment";
                
                var query = $"CREATE (e:Environment {{" +
                    $"id: '{EscapeCypher(id)}', " +
                    $"name: '{EscapeCypher(name)}', " +
                    $"label: '{EscapeCypher(name)}', " +
                    $"caption: '{EscapeCypher(name)}', " +
                    $"title: '{EscapeCypher(name)}', " +
                    $"description: '{description}', " +
                    $"type: '{type}'" +
                    $"}})";
                    
                await ExecuteGraphQuery(query);
                nodeCount++;
            }
        }
        
        // Create OWNED_BY relationships (Service -> Team, Resource -> Team)
        if (entities.TryGetProperty("services", out var servicesForOwnership))
        {
            foreach (var service in servicesForOwnership.EnumerateArray())
            {
                if (service.TryGetProperty("id", out var serviceId) && 
                    service.TryGetProperty("ownerRef", out var ownerRef))
                {
                    var sid = serviceId.GetString();
                    var owner = ownerRef.GetString();
                    await ExecuteGraphQuery($"MATCH (s:Service {{id: '{EscapeCypher(sid)}'}}), (t:Team {{id: '{EscapeCypher(owner)}'}}) CREATE (s)-[:OWNED_BY]->(t)");
                    relationshipCount++;
                }
            }
        }
        
        if (entities.TryGetProperty("resources", out var resourcesForOwnership))
        {
            foreach (var resource in resourcesForOwnership.EnumerateArray())
            {
                if (resource.TryGetProperty("id", out var resourceId) && 
                    resource.TryGetProperty("ownerRef", out var ownerRef))
                {
                    var rid = resourceId.GetString();
                    var owner = ownerRef.GetString();
                    await ExecuteGraphQuery($"MATCH (r:Resource {{id: '{EscapeCypher(rid)}'}}), (t:Team {{id: '{EscapeCypher(owner)}'}}) CREATE (r)-[:OWNED_BY]->(t)");
                    relationshipCount++;
                }
            }
        }
        
        // Create DEPENDS_ON and USES relationships from dependencies array
        foreach (var dep in dependencies.EnumerateArray())
        {
            if (!dep.TryGetProperty("from", out var from) || !dep.TryGetProperty("to", out var to))
                continue;
                
            var fromValue = from.GetString();
            var toValue = to.GetString();
            var kind = dep.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : "service-to-service";
            
            if (string.IsNullOrEmpty(fromValue) || string.IsNullOrEmpty(toValue))
                continue;
            
            if (kind == "service-to-resource")
            {
                // Service uses resource
                await ExecuteGraphQuery($"MATCH (s:Service {{id: '{EscapeCypher(fromValue)}'}}), (r:Resource {{id: '{EscapeCypher(toValue)}'}}) CREATE (s)-[:USES]->(r)");
                relationshipCount++;
            }
            else
            {
                // Service depends on service (extract service name from "service/interface" format)
                var targetService = toValue.Contains('/') ? toValue.Split('/')[0] : toValue;
                await ExecuteGraphQuery($"MATCH (a:Service {{id: '{EscapeCypher(fromValue)}'}}), (b:Service {{id: '{EscapeCypher(targetService)}'}}) CREATE (a)-[:DEPENDS_ON]->(b)");
                relationshipCount++;
            }
        }
        
        return (nodeCount, relationshipCount);
    }

    private async Task ExecuteGraphQuery(string cypherQuery)
    {
        if (_db == null) throw new InvalidOperationException("Database not connected");
        
        try
        {
            // FalkorDB uses GRAPH.QUERY command
            var result = await _db.ExecuteAsync("GRAPH.QUERY", _graphName, cypherQuery);
        }
        catch (Exception ex)
        {
            // Log but don't fail - graph features are optional
            Console.WriteLine($"   ⚠️  Graph query failed: {cypherQuery}");
            Console.WriteLine($"      Error: {ex.Message}");
        }
    }

    private string EscapeCypher(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // Escape single quotes and backslashes for Cypher
        return value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", " ").Replace("\r", "");
    }

    public async Task<List<FlightPlanChunk>> Search(
        string query, 
        string ollamaUrl = "http://localhost:11434", 
        string embeddingModel = "nomic-embed-text", 
        int topK = 5)
    {
        await EnsureConnection();
        if (_db == null) throw new InvalidOperationException("Database not connected");
        
        // Generate query embedding
        var embedder = new EmbeddingService(ollamaUrl, embeddingModel);
        var queryEmbedding = await embedder.GenerateEmbedding(query);
        
        // Load all chunks from FalkorDB
        var chunks = await LoadAllChunks();
        
        // Keyword matching
        var keywordMatches = FindKeywordMatches(query, chunks);
        
        // Analytical query detection
        var analyticalKeywords = new[] { "how many", "total", "count", "overview", "summary", "all services", "all resources" };
        var isAnalyticalQuery = analyticalKeywords.Any(k => query.ToLowerInvariant().Contains(k));
        
        // Calculate similarity scores
        foreach (var chunk in chunks)
        {
            var semanticScore = EmbeddingService.CosineSimilarity(queryEmbedding, chunk.Embedding);
            
            if (isAnalyticalQuery && chunk.Id == "overview")
            {
                chunk.Score = semanticScore * 2.0f;
            }
            else if (keywordMatches.Contains(chunk.Id))
            {
                chunk.Score = semanticScore * 1.5f;
            }
            else
            {
                chunk.Score = semanticScore;
            }
        }
        
        // Return top-K results
        return chunks
            .OrderByDescending(c => c.Score)
            .Take(topK)
            .ToList();
    }

    private async Task<List<FlightPlanChunk>> LoadAllChunks()
    {
        if (_db == null) throw new InvalidOperationException("Database not connected");
        
        var chunks = new List<FlightPlanChunk>();
        
        // Use Redis SCAN to find all chunk keys
        var server = _redis!.GetServer(_redis.GetEndPoints().First());
        var keys = server.Keys(pattern: $"{ChunkKeyPrefix}*");
        
        foreach (var key in keys)
        {
            var chunkData = await _db.HashGetAllAsync(key);
            if (chunkData.Length == 0) continue;
            
            var chunk = new FlightPlanChunk
            {
                Id = chunkData.FirstOrDefault(h => h.Name == "id").Value!,
                Type = chunkData.FirstOrDefault(h => h.Name == "type").Value!,
                Content = chunkData.FirstOrDefault(h => h.Name == "content").Value!,
                SourceFile = chunkData.FirstOrDefault(h => h.Name == "sourceFile").Value,
                SourceSection = chunkData.FirstOrDefault(h => h.Name == "sourceSection").Value,
            };
            
            var embeddingJson = chunkData.FirstOrDefault(h => h.Name == "embedding").Value;
            if (!embeddingJson.IsNullOrEmpty)
            {
                chunk.Embedding = JsonSerializer.Deserialize<float[]>(embeddingJson.ToString()) ?? Array.Empty<float>();
            }
            
            var metadataJson = chunkData.FirstOrDefault(h => h.Name == "metadata").Value;
            if (!metadataJson.IsNullOrEmpty)
            {
                chunk.Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson.ToString()) ?? new Dictionary<string, string>();
            }
            
            chunks.Add(chunk);
        }
        
        return chunks;
    }

    private List<string> FindKeywordMatches(string query, List<FlightPlanChunk> chunks)
    {
        var matches = new List<string>();
        var queryLower = query.ToLower();
        
        var stopwords = new HashSet<string> { 
            "the", "and", "or", "a", "an", "is", "are", "was", "were", 
            "tell", "me", "about", "what", "how", "when", "where", "which", 
            "service", "services", "resource", "resources", "it", "its", 
            "depends", "on", "for", "to", "from", "in", "of", "api"
        };
        
        var queryWords = query.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLower().Trim('.', ',', '?', '!'))
            .Where(w => w.Length > 3 && !stopwords.Contains(w))
            .Distinct()
            .ToList();
        
        if (queryWords.Count == 0)
            return matches;
        
        foreach (var chunk in chunks)
        {
            var chunkIdLower = chunk.Id.ToLower();
            if (queryWords.All(word => chunkIdLower.Contains(word)))
            {
                matches.Add(chunk.Id);
            }
        }
        
        return matches;
    }

    public async Task<VectorStoreMetadata?> GetMetadata()
    {
        await EnsureConnection();
        if (_db == null) throw new InvalidOperationException("Database not connected");
        
        var metadataJson = await _db.StringGetAsync(MetadataKey);
        if (metadataJson.IsNullOrEmpty)
            return null;
        
        return JsonSerializer.Deserialize<VectorStoreMetadata>(metadataJson.ToString());
    }

    public bool IndexExists()
    {
        try
        {
            EnsureConnection().GetAwaiter().GetResult();
            if (_db == null) return false;
            
            return _db.KeyExists(MetadataKey);
        }
        catch
        {
            return false;
        }
    }

    public void DeleteIndex()
    {
        DeleteIndexInternal().GetAwaiter().GetResult();
    }

    private async Task DeleteIndexInternal()
    {
        await EnsureConnection();
        if (_db == null) return;
        
        // Delete the entire graph
        try
        {
            await _db.ExecuteAsync("GRAPH.DELETE", _graphName);
            Console.WriteLine($"   Deleted graph: {_graphName}");
        }
        catch
        {
            // Graph may not exist, that's fine
        }
        
        // Delete metadata
        await _db.KeyDeleteAsync(MetadataKey);
        
        // Delete all chunks
        var server = _redis!.GetServer(_redis.GetEndPoints().First());
        var chunkKeys = server.Keys(pattern: $"{ChunkKeyPrefix}*").ToArray();
        if (chunkKeys.Length > 0)
        {
            await _db.KeyDeleteAsync(chunkKeys);
        }
    }

    // ============================================================
    // Public Graph Query Methods
    // ============================================================

    /// <summary>
    /// Execute a custom Cypher query against the graph database
    /// </summary>
    public async Task<GraphQueryResponse> ExecuteCypherQuery(string cypherQuery, Dictionary<string, object>? parameters = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await EnsureConnection();
            if (_db == null) throw new InvalidOperationException("Database not connected");

            // For now, we'll do simple string substitution for parameters
            // FalkorDB's Redis protocol doesn't support parameterized queries directly
            var query = cypherQuery;
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    var value = param.Value switch
                    {
                        string s => $"'{EscapeCypher(s)}'",
                        int i => i.ToString(),
                        long l => l.ToString(),
                        double d => d.ToString(),
                        bool b => b.ToString().ToLower(),
                        _ => $"'{EscapeCypher(param.Value?.ToString() ?? "")}'"
                    };
                    query = query.Replace($"${param.Key}", value);
                }
            }

            var result = await _db.ExecuteAsync("GRAPH.QUERY", _graphName, query);
            var results = ParseGraphQueryResult(result);
            
            sw.Stop();
            return new GraphQueryResponse
            {
                Success = true,
                Results = results,
                RowCount = results.Count,
                ExecutionTimeMs = sw.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new GraphQueryResponse
            {
                Success = false,
                Error = ex.Message,
                ExecutionTimeMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }

    /// <summary>
    /// Get statistics about the graph (node counts, relationship counts)
    /// </summary>
    public async Task<GraphStatistics> GetGraphStatistics()
    {
        var stats = new GraphStatistics();
        
        try
        {
            // Get total node count - simpler query
            var totalNodesQuery = "MATCH (n) RETURN count(n) as total";
            var totalNodesResult = await ExecuteCypherQuery(totalNodesQuery);
            if (totalNodesResult.Success && totalNodesResult.Results != null && totalNodesResult.Results.Count > 0)
            {
                var firstRow = totalNodesResult.Results[0];
                if (firstRow.TryGetValue("total", out var totalObj))
                {
                    stats.NodeCount = Convert.ToInt32(totalObj);
                }
            }

            // Get node counts by specific labels
            var labels = new[] { "Service", "Team", "Resource", "Environment" };
            foreach (var label in labels)
            {
                var labelQuery = $"MATCH (n:{label}) RETURN count(n) as count";
                var labelResult = await ExecuteCypherQuery(labelQuery);
                if (labelResult.Success && labelResult.Results != null && labelResult.Results.Count > 0)
                {
                    var firstRow = labelResult.Results[0];
                    if (firstRow.TryGetValue("count", out var countObj))
                    {
                        var count = Convert.ToInt32(countObj);
                        if (count > 0)
                        {
                            stats.NodesByLabel[label] = count;
                        }
                    }
                }
            }

            // Get total relationship count
            var totalRelsQuery = "MATCH ()-[r]->() RETURN count(r) as total";
            var totalRelsResult = await ExecuteCypherQuery(totalRelsQuery);
            if (totalRelsResult.Success && totalRelsResult.Results != null && totalRelsResult.Results.Count > 0)
            {
                var firstRow = totalRelsResult.Results[0];
                if (firstRow.TryGetValue("total", out var totalObj))
                {
                    stats.RelationshipCount = Convert.ToInt32(totalObj);
                }
            }

            // Get relationship counts by type
            var relationshipTypes = new[] { "DEPENDS_ON", "OWNED_BY", "USES" };
            foreach (var relType in relationshipTypes)
            {
                var relQuery = $"MATCH ()-[r:{relType}]->() RETURN count(r) as count";
                var relResult = await ExecuteCypherQuery(relQuery);
                if (relResult.Success && relResult.Results != null && relResult.Results.Count > 0)
                {
                    var firstRow = relResult.Results[0];
                    if (firstRow.TryGetValue("count", out var countObj))
                    {
                        var count = Convert.ToInt32(countObj);
                        if (count > 0)
                        {
                            stats.RelationshipsByType[relType] = count;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting graph statistics: {ex.Message}");
        }

        return stats;
    }

    /// <summary>
    /// Get all dependencies of a service or resource (what it depends on)
    /// </summary>
    public async Task<List<GraphNode>> GetServiceDependencies(string serviceId)
    {
        // Step 1: Try exact ID match - query both services and resources
        var query = @"
            MATCH (s)-[r:DEPENDS_ON|USES]->(dep)
            WHERE s.id = $serviceId AND (s:Service OR s:Resource) AND (dep:Service OR dep:Resource)
            RETURN 
                dep.id as id, 
                dep.name as name, 
                dep.description as description,
                labels(dep)[0] as type";
        var result = await ExecuteCypherQuery(query, new Dictionary<string, object> { { "serviceId", serviceId } });
        var nodes = ConvertToGraphNodesWithType(result);
        
        // Step 2: If no results, resolve the entity ID using entity resolution
        if (nodes.Count == 0)
        {
            var matchedIds = await ResolveEntityIds(serviceId);
            if (matchedIds.Count > 0)
            {
                Console.WriteLine($"🔍 Entity resolution: '{serviceId}' → {string.Join(", ", matchedIds.Select(id => $"'{id}'"))}");
                
                // Try each resolved ID
                foreach (var resolvedId in matchedIds)
                {
                    result = await ExecuteCypherQuery(query, new Dictionary<string, object> { { "serviceId", resolvedId } });
                    var resolvedNodes = ConvertToGraphNodesWithType(result);
                    if (resolvedNodes.Count > 0)
                    {
                        Console.WriteLine($"✅ Found {resolvedNodes.Count} dependencies for '{resolvedId}'");
                        nodes.AddRange(resolvedNodes);
                    }
                }
            }
            else
            {
                Console.WriteLine($"⚠️  Entity '{serviceId}' not found in graph");
            }
        }
        
        return nodes.DistinctBy(n => n.Id).ToList();
    }

    /// <summary>
    /// Get all services and resources that depend on a specific service or resource (reverse dependencies)
    /// </summary>
    public async Task<List<GraphNode>> GetServiceDependents(string serviceId)
    {
        // Step 1: Try exact ID match - find entities that depend on this service/resource
        var query = @"
            MATCH (s)-[r:DEPENDS_ON|USES]->(target)
            WHERE target.id = $serviceId AND (s:Service OR s:Resource) AND (target:Service OR target:Resource)
            RETURN 
                s.id as id, 
                s.name as name, 
                s.description as description,
                labels(s)[0] as type";
        var result = await ExecuteCypherQuery(query, new Dictionary<string, object> { { "serviceId", serviceId } });
        var nodes = ConvertToGraphNodesWithType(result);
        
        // Step 2: If no results, try to find the actual entity node to verify it exists
        if (nodes.Count == 0)
        {
            var matchedIds = await ResolveEntityIds(serviceId);
            if (matchedIds.Count > 0)
            {
                Console.WriteLine($"🔍 Entity resolution: '{serviceId}' → {string.Join(", ", matchedIds.Select(id => $"'{id}'"))}");
                
                // Try each resolved ID
                foreach (var resolvedId in matchedIds)
                {
                    result = await ExecuteCypherQuery(query, new Dictionary<string, object> { { "serviceId", resolvedId } });
                    var resolvedNodes = ConvertToGraphNodesWithType(result);
                    if (resolvedNodes.Count > 0)
                    {
                        Console.WriteLine($"✅ Found {resolvedNodes.Count} dependents for '{resolvedId}'");
                        nodes.AddRange(resolvedNodes);
                    }
                }
            }
            else
            {
                Console.WriteLine($"⚠️  Entity '{serviceId}' not found in graph");
            }
        }
        
        return nodes.DistinctBy(n => n.Id).ToList();
    }

    /// <summary>
    /// Entity resolution: Find actual service IDs in graph matching a partial/informal name
    /// Uses multiple strategies: exact match, contains match, word boundary match
    /// </summary>
    public async Task<List<string>> ResolveServiceIds(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new List<string>();
        }

        serviceName = serviceName.Trim();
        var matchedIds = new List<string>();
        
        // Strategy 1: Exact match
        var exactQuery = "MATCH (s:Service) WHERE s.id = $name OR s.name = $name OR s.description = $name RETURN s.id LIMIT 5";
        var result = await ExecuteCypherQuery(exactQuery, new Dictionary<string, object> { { "name", serviceName } });
        matchedIds.AddRange((result.Results ?? new()).SelectMany(r => r.Values.Where(v => v is string).Cast<string>()));
        
        if (matchedIds.Count == 0)
        {
            // Strategy 2: Case-insensitive CONTAINS match
            var lowerName = serviceName.ToLowerInvariant();
            var containsQuery = "WITH toLower($pattern) AS p MATCH (s:Service) WHERE toLower(s.id) CONTAINS p OR toLower(s.name) CONTAINS p OR (s.description IS NOT NULL AND toLower(s.description) CONTAINS p) RETURN s.id LIMIT 5";
            result = await ExecuteCypherQuery(containsQuery, new Dictionary<string, object> { { "pattern", lowerName } });
            matchedIds.AddRange((result.Results ?? new()).SelectMany(r => r.Values.Where(v => v is string).Cast<string>()));
        }

        if (matchedIds.Count == 0)
        {
            // Strategy 2b: Normalized CONTAINS match (handles SearchBatchApi vs "Search Batch API" / "search-batch-api")
            // Normalize both sides by stripping common separators.
            var normalizedPattern = System.Text.RegularExpressions.Regex.Replace(serviceName.ToLowerInvariant(), @"[^a-z0-9]", "");
            if (!string.IsNullOrWhiteSpace(normalizedPattern))
            {
                var normalizedContainsQuery =
                    "WITH toLower($pattern) AS p " +
                    "MATCH (s:Service) " +
                    "WHERE " +
                    "replace(replace(replace(replace(toLower(s.id),' ',''),'-',''),'_',''),'/','') CONTAINS p " +
                    "OR replace(replace(replace(replace(toLower(s.name),' ',''),'-',''),'_',''),'/','') CONTAINS p " +
                    "OR (s.description IS NOT NULL AND replace(replace(replace(replace(toLower(s.description),' ',''),'-',''),'_',''),'/','') CONTAINS p) " +
                    "RETURN s.id LIMIT 5";

                result = await ExecuteCypherQuery(normalizedContainsQuery, new Dictionary<string, object> { { "pattern", normalizedPattern } });
                matchedIds.AddRange((result.Results ?? new()).SelectMany(r => r.Values.Where(v => v is string).Cast<string>()));
            }
        }
        
        if (matchedIds.Count == 0 && serviceName.Contains(' '))
        {
            // Strategy 3: Try each word separately for compound names like "Adjudication API"
            var words = serviceName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words.Where(w => w.Length > 3))
            {
                var wordQuery = "WITH toLower($pattern) AS p MATCH (s:Service) WHERE toLower(s.id) CONTAINS p OR toLower(s.name) CONTAINS p OR (s.description IS NOT NULL AND toLower(s.description) CONTAINS p) RETURN s.id LIMIT 5";
                result = await ExecuteCypherQuery(wordQuery, new Dictionary<string, object> { { "pattern", word.ToLowerInvariant() } });
                matchedIds.AddRange((result.Results ?? new()).SelectMany(r => r.Values.Where(v => v is string).Cast<string>()));
                if (matchedIds.Count > 0) break;
            }
        }
        
        return matchedIds.Distinct().ToList();
    }

    /// <summary>
    /// Entity resolution: Find actual service or resource IDs in graph matching a partial/informal name
    /// Uses multiple strategies: exact match, contains match, word boundary match
    /// </summary>
    public async Task<List<string>> ResolveEntityIds(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            return new List<string>();
        }

        entityName = entityName.Trim();
        var matchedIds = new List<string>();
        
        // Strategy 1: Exact match on both Service and Resource nodes
        var exactQuery = @"
            MATCH (n)
            WHERE (n:Service OR n:Resource) AND (n.id = $name OR n.name = $name OR n.description = $name)
            RETURN n.id LIMIT 5";
        var result = await ExecuteCypherQuery(exactQuery, new Dictionary<string, object> { { "name", entityName } });
        matchedIds.AddRange((result.Results ?? new()).SelectMany(r => r.Values.Where(v => v is string).Cast<string>()));
        
        if (matchedIds.Count == 0)
        {
            // Strategy 2: Case-insensitive CONTAINS match
            var lowerName = entityName.ToLowerInvariant();
            var containsQuery = @"
                WITH toLower($pattern) AS p 
                MATCH (n) 
                WHERE (n:Service OR n:Resource) AND (
                    toLower(n.id) CONTAINS p OR 
                    toLower(n.name) CONTAINS p OR 
                    (n.description IS NOT NULL AND toLower(n.description) CONTAINS p)
                )
                RETURN n.id LIMIT 5";
            result = await ExecuteCypherQuery(containsQuery, new Dictionary<string, object> { { "pattern", lowerName } });
            matchedIds.AddRange((result.Results ?? new()).SelectMany(r => r.Values.Where(v => v is string).Cast<string>()));
        }

        if (matchedIds.Count == 0)
        {
            // Strategy 3: Normalized CONTAINS match (handles different naming conventions)
            var normalizedPattern = System.Text.RegularExpressions.Regex.Replace(entityName.ToLowerInvariant(), @"[^a-z0-9]", "");
            if (!string.IsNullOrWhiteSpace(normalizedPattern))
            {
                var normalizedContainsQuery = @"
                    WITH toLower($pattern) AS p 
                    MATCH (n) 
                    WHERE (n:Service OR n:Resource) AND (
                        replace(replace(replace(replace(toLower(n.id),' ',''),'-',''),'_',''),'/','') CONTAINS p 
                        OR replace(replace(replace(replace(toLower(n.name),' ',''),'-',''),'_',''),'/','') CONTAINS p 
                        OR (n.description IS NOT NULL AND replace(replace(replace(replace(toLower(n.description),' ',''),'-',''),'_',''),'/','') CONTAINS p)
                    )
                    RETURN n.id LIMIT 5";

                result = await ExecuteCypherQuery(normalizedContainsQuery, new Dictionary<string, object> { { "pattern", normalizedPattern } });
                matchedIds.AddRange((result.Results ?? new()).SelectMany(r => r.Values.Where(v => v is string).Cast<string>()));
            }
        }
        
        if (matchedIds.Count == 0 && entityName.Contains(' '))
        {
            // Strategy 4: Try each word separately for compound names
            var words = entityName.Split(new[] { ' ', '-', '_', '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words.Where(w => w.Length > 3))
            {
                var wordQuery = @"
                    WITH toLower($pattern) AS p 
                    MATCH (n) 
                    WHERE (n:Service OR n:Resource) AND (
                        toLower(n.id) CONTAINS p OR 
                        toLower(n.name) CONTAINS p OR 
                        (n.description IS NOT NULL AND toLower(n.description) CONTAINS p)
                    )
                    RETURN n.id LIMIT 5";
                result = await ExecuteCypherQuery(wordQuery, new Dictionary<string, object> { { "pattern", word.ToLowerInvariant() } });
                matchedIds.AddRange((result.Results ?? new()).SelectMany(r => r.Values.Where(v => v is string).Cast<string>()));
                if (matchedIds.Count > 0) break;
            }
        }
        
        return matchedIds.Distinct().ToList();
    }

    /// <summary>
    /// Get impact analysis - all services that would be affected if this service fails
    /// </summary>
    public async Task<List<GraphNode>> GetImpactAnalysis(string serviceId)
    {
        // Try entity resolution first
        var resolvedIds = await ResolveServiceIds(serviceId);
        var allNodes = new List<GraphNode>();
        
        foreach (var id in resolvedIds.Any() ? resolvedIds : new List<string> { serviceId })
        {
            // Get all services that transitively depend on this service
            var query = $"MATCH path = (s:Service)-[:DEPENDS_ON*]->(target:Service {{id: $serviceId}}) RETURN DISTINCT s.id as id, s.name as name, s.description as description, length(path) as distance ORDER BY distance";
            var result = await ExecuteCypherQuery(query, new Dictionary<string, object> { { "serviceId", id } });
            allNodes.AddRange(ConvertToGraphNodes(result, "Service"));
        }
        
        return allNodes.DistinctBy(n => n.Id).ToList();
    }

    /// <summary>
    /// Get all resources owned by a team
    /// </summary>
    public async Task<Dictionary<string, List<GraphNode>>> GetTeamOwnership(string teamId)
    {
        var ownership = new Dictionary<string, List<GraphNode>>();
        
        // Get services owned by team
        var servicesQuery = $"MATCH (s:Service)-[:OWNED_BY]->(t:Team {{id: $teamId}}) RETURN s.id as id, s.name as name, s.description as description";
        var servicesResult = await ExecuteCypherQuery(servicesQuery, new Dictionary<string, object> { { "teamId", teamId } });
        ownership["services"] = ConvertToGraphNodes(servicesResult, "Service");
        
        // Get resources owned by team
        var resourcesQuery = $"MATCH (r:Resource)-[:OWNED_BY]->(t:Team {{id: $teamId}}) RETURN r.id as id, r.name as name, r.description as description";
        var resourcesResult = await ExecuteCypherQuery(resourcesQuery, new Dictionary<string, object> { { "teamId", teamId } });
        ownership["resources"] = ConvertToGraphNodes(resourcesResult, "Resource");
        
        return ownership;
    }

    /// <summary>
    /// Find path between two services
    /// </summary>
    public async Task<GraphQueryResponse> FindPath(string fromServiceId, string toServiceId)
    {
        var query = $"MATCH path = shortestPath((from:Service {{id: $fromServiceId}})-[*]-(to:Service {{id: $toServiceId}})) RETURN path";
        return await ExecuteCypherQuery(query, new Dictionary<string, object> 
        { 
            { "fromServiceId", fromServiceId },
            { "toServiceId", toServiceId }
        });
    }

    /// <summary>
    /// Get common graph query patterns
    /// </summary>
    public static List<GraphPattern> GetCommonPatterns()
    {
        return new List<GraphPattern>
        {
            new GraphPattern
            {
                Name = "list_all_services",
                Description = "List all services in the system",
                CypherQuery = "MATCH (s:Service) RETURN s.id as id, s.name as name, s.description as description ORDER BY s.name",
                RequiredParameters = new()
            },
            new GraphPattern
            {
                Name = "list_all_teams",
                Description = "List all teams",
                CypherQuery = "MATCH (t:Team) RETURN t.id as id, t.name as name ORDER BY t.name",
                RequiredParameters = new()
            },
            new GraphPattern
            {
                Name = "service_dependencies",
                Description = "Get dependencies of a specific service",
                CypherQuery = "MATCH (s:Service {id: $serviceId})-[:DEPENDS_ON]->(dep:Service) RETURN dep.id as id, dep.name as name",
                RequiredParameters = new() { "serviceId" }
            },
            new GraphPattern
            {
                Name = "service_dependents",
                Description = "Get services that depend on a specific service",
                CypherQuery = "MATCH (s:Service)-[:DEPENDS_ON]->(target:Service {id: $serviceId}) RETURN s.id as id, s.name as name",
                RequiredParameters = new() { "serviceId" }
            },
            new GraphPattern
            {
                Name = "team_ownership",
                Description = "Get all services and resources owned by a team",
                CypherQuery = "MATCH (n)-[:OWNED_BY]->(t:Team {id: $teamId}) RETURN labels(n)[0] as type, n.id as id, n.name as name",
                RequiredParameters = new() { "teamId" }
            },
            new GraphPattern
            {
                Name = "resource_usage",
                Description = "Get all services that use a specific resource",
                CypherQuery = "MATCH (s:Service)-[:USES]->(r:Resource {id: $resourceId}) RETURN s.id as id, s.name as name",
                RequiredParameters = new() { "resourceId" }
            },
            new GraphPattern
            {
                Name = "impact_analysis",
                Description = "Find all services that would be affected if a service fails",
                CypherQuery = "MATCH path = (s:Service)-[:DEPENDS_ON*]->(target:Service {id: $serviceId}) RETURN DISTINCT s.id as id, s.name as name, length(path) as distance ORDER BY distance",
                RequiredParameters = new() { "serviceId" }
            }
        };
    }

    private List<GraphNode> ConvertToGraphNodes(GraphQueryResponse response, string label)
    {
        var nodes = new List<GraphNode>();
        if (response.Success && response.Results != null)
        {
            foreach (var row in response.Results)
            {
                nodes.Add(new GraphNode
                {
                    Label = label,
                    Id = row.GetValueOrDefault("id")?.ToString() ?? "",
                    Properties = row
                });
            }
        }
        return nodes;
    }

    /// <summary>
    /// Convert graph query response to GraphNodes with type from query results
    /// </summary>
    private List<GraphNode> ConvertToGraphNodesWithType(GraphQueryResponse response)
    {
        var nodes = new List<GraphNode>();
        if (response.Success && response.Results != null)
        {
            foreach (var row in response.Results)
            {
                var type = row.GetValueOrDefault("type")?.ToString() ?? "Unknown";
                nodes.Add(new GraphNode
                {
                    Label = type,
                    Id = row.GetValueOrDefault("id")?.ToString() ?? "",
                    Properties = row
                });
            }
        }
        return nodes;
    }

    private List<Dictionary<string, object>> ParseGraphQueryResult(RedisResult result)
    {
        var results = new List<Dictionary<string, object>>();
        
        try
        {
            if (result.IsNull)
            {
                Console.WriteLine("Graph query returned null result");
                return results;
            }

            var array = (RedisResult[])result;
            Console.WriteLine($"Graph query result array length: {array.Length}");
            
            if (array.Length < 1) return results;

            // FalkorDB has two different response formats:
            // 1. For scalar results (COUNT, simple values): [data_rows_array, header_array, stats]  
            //    - data_rows contains scalar values
            //    - header contains wrapped column names like [[name]]
            // 2. For node/property results: [header_array, data_rows_array, stats]
            //    - header contains simple column name strings like "id", "name"  
            //    - data_rows contains arrays of values
            
            RedisResult[] dataRows;
            string[] columns;
            
            // Check if array[0] looks like headers (strings) or data (wrapped values)
            var firstEntry = array[0];
            var isHeaderFirst = false;
            
            if (firstEntry.Type == ResultType.Array)
            {
                var firstArray = (RedisResult[])firstEntry;
                if (firstArray.Length > 0 && firstArray[0].Type == ResultType.BulkString)
                {
                    // Check if it's simple strings (column names) vs wrapped data
                    var firstValue = firstArray[0].ToString();
                    // Common column names that indicate this is a header
                    if (firstValue == "id" || firstValue == "name" || firstValue == "description" || 
                        firstValue == "count" || firstValue == "total" || firstValue == "distance")
                    {
                        isHeaderFirst = true;
                        Console.WriteLine("Detected node/property query format (header first)");
                    }
                }
            }
            
            if (isHeaderFirst && array.Length > 1)
            {
                // Format 2: [header, data, stats]
                var headerArray = (RedisResult[])array[0];
                columns = headerArray.Select(h => h.ToString()).ToArray();
                Console.WriteLine($"Column names: {string.Join(", ", columns)}");
                
                // Check array[1] type and contents
                Console.WriteLine($"array[1] type: {array[1].Resp2Type}, IsNull: {array[1].IsNull}");
                if (array[1].Resp2Type == ResultType.Array && !array[1].IsNull)
                {
                    dataRows = (RedisResult[])array[1];
                    Console.WriteLine($"Data rows count: {dataRows.Length}");
                }
                else
                {
                    // Empty result set - no problem, just no data
                    dataRows = Array.Empty<RedisResult>();
                    Console.WriteLine("No data rows in result");
                }
            }
            else
            {
                // Format 1: [data, header, stats] - original format for scalar queries
                dataRows = (RedisResult[])array[0];
                Console.WriteLine($"Data rows count: {dataRows.Length}");
                
                // Column headers are in array[1] if present
                columns = Array.Empty<string>();
                if (array.Length > 1 && !array[1].IsNull)
                {
                    try
                    {
                        var headerArray = (RedisResult[])array[1];
                        Console.WriteLine($"Header array length: {headerArray.Length}");
                        columns = new string[headerArray.Length];
                        for (int i = 0; i < headerArray.Length; i++)
                        {
                            Console.WriteLine($"Header[{i}] type: {headerArray[i].Type}, IsNull: {headerArray[i].IsNull}");
                            
                            // Try different parsing approaches based on type
                            if (headerArray[i].Type == ResultType.Array)
                            {
                                // Each header element is an array, FalkorDB format: [column_name]
                                var headerItem = (RedisResult[])headerArray[i];
                                Console.WriteLine($"  Header item array length: {headerItem.Length}");
                                if (headerItem.Length > 0)
                                {
                                    // Use first element as column name
                                    columns[i] = (string)headerItem[0];
                                }
                            }
                            else if (headerArray[i].Type == ResultType.BulkString)
                            {
                                // Header might be just a string
                                columns[i] = (string)headerArray[i];
                            }
                            else
                            {
                                // Fallback to string representation
                                columns[i] = headerArray[i].ToString();
                            }
                            Console.WriteLine($"  Parsed column name: {columns[i]}");
                        }
                        Console.WriteLine($"Parsed columns: {string.Join(", ", columns)}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error parsing column headers: {ex.Message}");
                        Console.WriteLine($"  Stack: {ex.StackTrace}");
                    }
                }

                // If we don't have column names, try to infer from common patterns
                if (columns.Length == 0 && dataRows.Length > 0)
                {
                    try
                    {
                        var firstRow = (RedisResult[])dataRows[0];
                        columns = Enumerable.Range(0, firstRow.Length).Select(i => $"col{i}").ToArray();
                        Console.WriteLine($"Using default column names: {string.Join(", ", columns)}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error parsing first row for column names: {ex.Message}");
                    }
                }
            }

            foreach (var row in dataRows)
            {
                var rowData = new Dictionary<string, object>();
                try
                {
                    Console.WriteLine($"Row type: {row.Type}, IsNull: {row.IsNull}, Raw: {row}");
                    
                    // FalkorDB returns simple scalar results as non-array (e.g., count returned as BulkString or Integer)
                    // For simple queries like "RETURN count(n) as total", the result format is:
                    // - headers: [[value]] (the actual count value!)
                    // - data_rows: ["alias_name"] (the column name!)
                    // This is inverted from what we'd expect, so we need special handling
                    
                    if (row.Type == ResultType.Array)
                    {
                        // Normal query: row contains array of values
                        var values = (RedisResult[])row;
                        Console.WriteLine($"Row has {values.Length} values:");
                        
                        for (int i = 0; i < values.Length; i++)
                        {
                            Console.WriteLine($"  Value[{i}] type: {values[i].Type}, IsNull: {values[i].IsNull}, Raw: {values[i]}");
                        }
                        
                        for (int i = 0; i < values.Length && i < columns.Length; i++)
                        {
                            var value = ParseRedisValue(values[i]);
                            rowData[columns[i]] = value;
                            Console.WriteLine($"  {columns[i]} = {value} (type: {value?.GetType().Name ?? "null"})");
                        }
                    }
                    else
                    {
                        // Scalar query: FalkorDB puts the actual value in the header and the alias in the row
                        // So columns[] contains the values, and row contains the column name
                        Console.WriteLine("Scalar result detected - header contains value, row contains name");
                        
                        string columnName = row.ToString();
                        if (columns.Length > 0)
                        {
                            // The "column" is actually the value - try to parse it as a number
                            if (long.TryParse(columns[0], out long value))
                            {
                                rowData[columnName] = value;
                                Console.WriteLine($"  {columnName} = {value} (parsed from header)");
                            }
                            else if (double.TryParse(columns[0], out double dblValue))
                            {
                                rowData[columnName] = dblValue;
                                Console.WriteLine($"  {columnName} = {dblValue} (parsed from header)");
                            }
                            else
                            {
                                rowData[columnName] = columns[0];
                                Console.WriteLine($"  {columnName} = {columns[0]} (string from header)");
                            }
                        }
                    }
                    
                    results.Add(rowData);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error parsing row: {ex.Message}");
                    Console.WriteLine($"  Stack: {ex.StackTrace}");
                }
            }
            
            Console.WriteLine($"Parsed {results.Count} result rows");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing graph query result: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }

        return results;
    }

    private object ParseRedisValue(RedisResult value)
    {
        if (value.IsNull) return string.Empty;
        
        try
        {
            // Try to parse as different types
            if (value.Type == ResultType.Integer)
                return (long)value;
            
            if (value.Type == ResultType.BulkString)
                return value.ToString();
            
            if (value.Type == ResultType.Array)
            {
                var array = (RedisResult[])value;
                return array.Select(ParseRedisValue).ToArray();
            }
            
            return value.ToString();
        }
        catch
        {
            return value.ToString();
        }
    }
}
