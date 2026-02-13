using FlightPlan.Models;

namespace FlightPlan.Services;

/// <summary>
/// Interface for vector stores supporting RAG and graph queries
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Index a FlightPlan and optionally its documentation
    /// </summary>
    /// <param name="compiledJson">Compiled FlightPlan JSON file</param>
    /// <param name="docsDirectory">Optional directory containing markdown documentation</param>
    /// <param name="baseUrl">Base URL for documentation links</param>
    /// <param name="ollamaUrl">Ollama API URL</param>
    /// <param name="embeddingModel">Embedding model to use</param>
    /// <returns>Number of chunks indexed</returns>
    Task<int> IndexFlightPlan(FileInfo compiledJson, string? docsDirectory, string? baseUrl, string ollamaUrl = "http://localhost:11434", string embeddingModel = "nomic-embed-text");

    /// <summary>
    /// Search for relevant chunks using semantic search
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="ollamaUrl">Ollama API URL</param>
    /// <param name="embeddingModel">Embedding model to use (must match indexed model)</param>
    /// <param name="topK">Number of results to return</param>
    /// <returns>List of relevant chunks with similarity scores</returns>
    Task<List<FlightPlanChunk>> Search(string query, string ollamaUrl = "http://localhost:11434", string embeddingModel = "nomic-embed-text", int topK = 5);

    /// <summary>
    /// Get metadata about the indexed content
    /// </summary>
    Task<VectorStoreMetadata?> GetMetadata();

    /// <summary>
    /// Check if an index exists
    /// </summary>
    bool IndexExists();

    /// <summary>
    /// Delete the index
    /// </summary>
    void DeleteIndex();

    /// <summary>
    /// Get the store type name
    /// </summary>
    string StoreType { get; }
}
