using System.Text.Json;
using FlightPlan.Models;

namespace FlightPlan.Services;

/// <summary>
/// Simple in-memory vector store with JSON file persistence
/// </summary>
public class VectorStore : IVectorStore
{
    private List<FlightPlanChunk> _chunks = new();
    private readonly string _indexPath;
    private VectorStoreMetadata? _metadata;

    public string StoreType => "json";

    public VectorStore(string indexPath = ".flightplan.index.json")
    {
        _indexPath = indexPath;
    }

    public async Task<int> IndexFlightPlan(FileInfo compiledJson, string? docsDirectory, string? baseUrl, string ollamaUrl = "http://localhost:11434", string embeddingModel = "nomic-embed-text")
    {
        Console.WriteLine("📖 Loading FlightPlan...");
        var jsonContent = await File.ReadAllTextAsync(compiledJson.FullName);
        var doc = JsonDocument.Parse(jsonContent);

        // Store metadata about the indexed file
        _metadata = new VectorStoreMetadata
        {
            SourceFile = compiledJson.FullName,
            IndexedAt = DateTime.UtcNow,
            EmbeddingModel = embeddingModel,
            ChunkCount = 0, // Will be updated after chunking
            IncludesDocumentation = !string.IsNullOrEmpty(docsDirectory),
            BaseUrl = baseUrl
        };

        Console.WriteLine("✂️  Chunking FlightPlan...");
        var chunker = new FlightPlanChunker();
        _chunks = chunker.ChunkFlightPlan(doc);
        Console.WriteLine($"   Created {_chunks.Count} FlightPlan chunks");

        // Add documentation chunks if directory provided
        if (!string.IsNullOrEmpty(docsDirectory))
        {
            Console.WriteLine($"📚 Chunking documentation from {docsDirectory}...");
            var markdownChunker = new MarkdownChunker();
            var docChunks = markdownChunker.ChunkMarkdownFiles(docsDirectory);
            _chunks.AddRange(docChunks);
            Console.WriteLine($"   Created {docChunks.Count} documentation chunks");
        }

        _metadata.ChunkCount = _chunks.Count;
        Console.WriteLine($"   Total: {_chunks.Count} chunks");

        Console.WriteLine("🧠 Generating embeddings...");
        var embedder = new EmbeddingService(ollamaUrl, embeddingModel);
        
        var progress = 0;
        var batchSize = 3; // Smaller batches to avoid overwhelming Ollama
        var delayMs = 250; // Longer delay between batches
        
        for (var i = 0; i < _chunks.Count; i++)
        {
            try
            {
                _chunks[i].Embedding = await embedder.GenerateEmbedding(_chunks[i].Content);
                progress++;
                
                if (progress % 10 == 0)
                {
                    Console.WriteLine($"   Embedded {progress}/{_chunks.Count} chunks...");
                }
                
                // Add delay every N chunks to avoid rate limiting
                if (progress % batchSize == 0)
                {
                    await Task.Delay(delayMs);
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.InternalServerError)
            {
                Console.WriteLine($"\n⚠️  Ollama error at chunk {progress}/{_chunks.Count}. Waiting 5s and retrying...");
                await Task.Delay(5000);
                
                // Retry once with longer wait
                try
                {
                    _chunks[i].Embedding = await embedder.GenerateEmbedding(_chunks[i].Content);
                    progress++;
                    Console.WriteLine($"   ✓ Retry successful, continuing...");
                }
                catch (Exception retryEx)
                {
                    Console.WriteLine($"\n⚠️  Skipping problematic chunk {progress}:");
                    Console.WriteLine($"   ID: {_chunks[i].Id}");
                    Console.WriteLine($"   Type: {_chunks[i].Type}");
                    Console.WriteLine($"   Content length: {_chunks[i].Content.Length} characters");
                    Console.WriteLine($"   Error: {retryEx.Message}");
                    Console.WriteLine($"   This chunk will not be searchable, but indexing will continue...\n");
                    
                    // Remove chunk and adjust counter
                    _chunks.RemoveAt(i);
                    i--;
                    // Don't increment progress since we removed the chunk
                }
            }
        }

        Console.WriteLine("💾 Saving index...");
        await SaveIndex();
        
        return _chunks.Count;
    }

    public async Task LoadIndex()
    {
        if (!File.Exists(_indexPath))
        {
            throw new FileNotFoundException($"Index not found at {_indexPath}. Run 'flight index' first.");
        }

        var json = await File.ReadAllTextAsync(_indexPath);
        var indexData = JsonSerializer.Deserialize<VectorStoreIndex>(json);
        
        if (indexData == null)
        {
            throw new InvalidOperationException("Failed to deserialize index data");
        }

        _chunks = indexData.Chunks ?? new List<FlightPlanChunk>();
        _metadata = indexData.Metadata;
    }

    public async Task<VectorStoreMetadata?> GetMetadata()
    {
        if (_metadata == null && File.Exists(_indexPath))
        {
            var json = await File.ReadAllTextAsync(_indexPath);
            var indexData = JsonSerializer.Deserialize<VectorStoreIndex>(json);
            _metadata = indexData?.Metadata;
        }
        return _metadata;
    }

    public bool IndexExists()
    {
        return File.Exists(_indexPath);
    }

    public async Task<List<FlightPlanChunk>> Search(string query, string ollamaUrl = "http://localhost:11434", string embeddingModel = "nomic-embed-text", int topK = 5)
    {
        if (_chunks.Count == 0)
        {
            await LoadIndex();
        }

        // HYBRID SEARCH: First try exact/fuzzy keyword matching, then fall back to semantic search
        var keywordMatches = FindKeywordMatches(query);
        
        // Detect analytical queries that should prioritize overview
        var analyticalKeywords = new[] { "how many", "total", "count", "overview", "summary", "all services", "all resources" };
        var isAnalyticalQuery = analyticalKeywords.Any(k => query.ToLowerInvariant().Contains(k));
        
        // If we have strong keyword matches, boost their scores
        if (keywordMatches.Any())
        {
            // Generate embedding for the query for semantic scoring
            var embedder = new EmbeddingService(ollamaUrl, embeddingModel);
            var queryEmbedding = await embedder.GenerateEmbedding(query);

            // Calculate similarity scores for all chunks
            foreach (var chunk in _chunks)
            {
                var semanticScore = EmbeddingService.CosineSimilarity(queryEmbedding, chunk.Embedding);
                
                // Boost overview chunk for analytical queries
                if (isAnalyticalQuery && chunk.Id == "overview")
                {
                    chunk.Score = semanticScore * 2.0f; // 100% boost for overview on analytical queries
                }
                // Boost keyword matches significantly
                else if (keywordMatches.Contains(chunk.Id))
                {
                    chunk.Score = semanticScore * 1.5f; // 50% boost for keyword matches
                }
                else
                {
                    chunk.Score = semanticScore;
                }
            }
        }
        else
        {
            // Pure semantic search
            var embedder = new EmbeddingService(ollamaUrl, embeddingModel);
            var queryEmbedding = await embedder.GenerateEmbedding(query);

            foreach (var chunk in _chunks)
            {
                var semanticScore = EmbeddingService.CosineSimilarity(queryEmbedding, chunk.Embedding);
                
                // Boost overview chunk for analytical queries
                if (isAnalyticalQuery && chunk.Id == "overview")
                {
                    chunk.Score = semanticScore * 2.0f; // 100% boost for overview
                }
                else
                {
                    chunk.Score = semanticScore;
                }
            }
        }

        // Return top-K most similar chunks
        return _chunks
            .OrderByDescending(c => c.Score)
            .Take(topK)
            .ToList();
    }

    private List<string> FindKeywordMatches(string query)
    {
        var matches = new List<string>();
        var queryLower = query.ToLower();
        
        // Extract key terms from query (remove stopwords and common phrases)
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
        
        // Need at least one significant word for keyword matching
        if (queryWords.Count == 0)
            return matches;
        
        foreach (var chunk in _chunks)
        {
            var chunkIdLower = chunk.Id.ToLower();
            
            // STRONG MATCH: All query words appear in the chunk ID (service name)
            if (queryWords.All(word => chunkIdLower.Contains(word)))
            {
                matches.Add(chunk.Id);
            }
        }
        
        return matches;
    }

    private async Task SaveIndex()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        
        var indexData = new VectorStoreIndex
        {
            Metadata = _metadata,
            Chunks = _chunks
        };
        
        var json = JsonSerializer.Serialize(indexData, options);
        await File.WriteAllTextAsync(_indexPath, json);
    }

    public void DeleteIndex()
    {
        if (File.Exists(_indexPath))
        {
            File.Delete(_indexPath);
        }
    }
}

public class VectorStoreMetadata
{
    public string SourceFile { get; set; } = string.Empty;
    public DateTime IndexedAt { get; set; }
    public string EmbeddingModel { get; set; } = string.Empty;
    public int ChunkCount { get; set; }
    public bool IncludesDocumentation { get; set; }
    public string? BaseUrl { get; set; }
    public string? GraphName { get; set; }
}

public class VectorStoreIndex
{
    public VectorStoreMetadata? Metadata { get; set; }
    public List<FlightPlanChunk>? Chunks { get; set; }
}
