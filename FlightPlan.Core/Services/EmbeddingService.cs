using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace FlightPlan.Services;

/// <summary>
/// Service for generating embeddings using Ollama's embedding API
/// </summary>
public class EmbeddingService
{
    private readonly string _ollamaUrl;
    private readonly string _embeddingModel;
    private readonly HttpClient _client;

    public EmbeddingService(string ollamaUrl = "http://localhost:11434", string embeddingModel = "nomic-embed-text")
    {
        _ollamaUrl = ollamaUrl;
        _embeddingModel = embeddingModel;
        _client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<float[]> GenerateEmbedding(string text)
    {
        var request = new EmbeddingRequest
        {
            Model = _embeddingModel,
            Prompt = text
        };

        var response = await _client.PostAsJsonAsync($"{_ollamaUrl}/api/embeddings", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>();
        return result?.Embedding ?? Array.Empty<float>();
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have the same length");

        var dotProduct = 0.0f;
        var magnitudeA = 0.0f;
        var magnitudeB = 0.0f;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            magnitudeA += a[i] * a[i];
            magnitudeB += b[i] * b[i];
        }

        magnitudeA = MathF.Sqrt(magnitudeA);
        magnitudeB = MathF.Sqrt(magnitudeB);

        if (magnitudeA == 0 || magnitudeB == 0)
            return 0;

        return dotProduct / (magnitudeA * magnitudeB);
    }

    private class EmbeddingRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;
    }

    private class EmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }
}
