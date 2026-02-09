using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlightPlan.Services;

/// <summary>
/// Enhances report sections with AI-generated narratives and insights using Ollama
/// </summary>
public class AiReportEnhancer
{
    private readonly HttpClient _httpClient;
    private readonly string _ollamaUrl;
    private readonly string _model;
    private readonly List<AiPromptLogEntry> _promptLog = new();

    public AiReportEnhancer(string ollamaUrl = "http://localhost:11434", string model = "llama3.2")
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ollamaUrl = ollamaUrl.TrimEnd('/');
        _model = model;
    }

    public List<AiPromptLogEntry> GetPromptLog() => _promptLog;

    /// <summary>
    /// Generate an executive summary narrative from structured data
    /// </summary>
    public async Task<string> GenerateExecutiveSummaryAsync(string reportType, Dictionary<string, string> data)
    {
        var prompt = BuildExecutiveSummaryPrompt(reportType, data);
        return await CallOllamaAsync("executive-summary", reportType, prompt);
    }

    /// <summary>
    /// Generate insights/analysis for a report section
    /// </summary>
    public async Task<string> GenerateSectionInsightsAsync(string reportType, string sectionName, string sectionData)
    {
        var prompt = BuildSectionInsightsPrompt(reportType, sectionName, sectionData);
        return await CallOllamaAsync($"section-insights:{sectionName}", reportType, prompt);
    }

    /// <summary>
    /// Generate findings/recommendations from structured data
    /// </summary>
    public async Task<string> GenerateFindingsNarrativeAsync(string reportType, List<string> findings)
    {
        var prompt = BuildFindingsPrompt(reportType, findings);
        return await CallOllamaAsync("findings-narrative", reportType, prompt);
    }

    /// <summary>
    /// Generate architecture analysis from service/resource data
    /// </summary>
    public async Task<string> GenerateArchitectureAnalysisAsync(Dictionary<string, object> architectureData)
    {
        var prompt = BuildArchitectureAnalysisPrompt(architectureData);
        return await CallOllamaAsync("architecture-analysis", "architecture-overview", prompt);
    }

    private async Task<string> CallOllamaAsync(string promptId, string reportType, string systemPrompt)
    {
        var requestPayload = new
        {
            model = _model,
            prompt = systemPrompt,
            stream = false,
            options = new
            {
                temperature = 0.7,
                top_p = 0.9
            }
        };

        var startTime = DateTime.UtcNow;
        
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_ollamaUrl}/api/generate", requestPayload);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
            var generatedText = result?.Response ?? string.Empty;

            var endTime = DateTime.UtcNow;

            // Log the interaction
            _promptLog.Add(new AiPromptLogEntry
            {
                Timestamp = startTime,
                ReportType = reportType,
                SectionId = promptId,
                Model = _model,
                Prompt = systemPrompt,
                Response = generatedText,
                DurationMs = (int)(endTime - startTime).TotalMilliseconds,
                Success = true
            });

            return generatedText;
        }
        catch (Exception ex)
        {
            var endTime = DateTime.UtcNow;
            
            // Log the failure
            _promptLog.Add(new AiPromptLogEntry
            {
                Timestamp = startTime,
                ReportType = reportType,
                SectionId = promptId,
                Model = _model,
                Prompt = systemPrompt,
                Response = string.Empty,
                Error = ex.Message,
                DurationMs = (int)(endTime - startTime).TotalMilliseconds,
                Success = false
            });

            Console.Error.WriteLine($"⚠️  AI enhancement failed for {promptId}: {ex.Message}");
            return string.Empty; // Return empty string on failure to allow report to continue
        }
    }

    private string BuildExecutiveSummaryPrompt(string reportType, Dictionary<string, string> data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert technical writer and enterprise architect. Generate a concise, professional executive summary for a system architecture report.");
        sb.AppendLine();
        sb.AppendLine($"Report Type: {reportType}");
        sb.AppendLine();
        sb.AppendLine("System Information:");
        foreach (var kvp in data)
        {
            sb.AppendLine($"- {kvp.Key}: {kvp.Value}");
        }
        sb.AppendLine();
        sb.AppendLine("Requirements:");
        sb.AppendLine("- Write 2-3 paragraphs (150-250 words)");
        sb.AppendLine("- Focus on business value and technical highlights");
        sb.AppendLine("- Be specific using the data provided");
        sb.AppendLine("- Use professional, clear language");
        sb.AppendLine("- Do not use marketing speak or buzzwords");
        sb.AppendLine("- Do not include headers or titles, just the narrative");
        sb.AppendLine();
        sb.AppendLine("Generate the executive summary now:");

        return sb.ToString();
    }

    private string BuildSectionInsightsPrompt(string reportType, string sectionName, string sectionData)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert system architect analyzing a FlightPlan architecture report.");
        sb.AppendLine();
        sb.AppendLine($"Report Type: {reportType}");
        sb.AppendLine($"Section: {sectionName}");
        sb.AppendLine();
        sb.AppendLine("Section Data:");
        sb.AppendLine(sectionData);
        sb.AppendLine();
        sb.AppendLine("Requirements:");
        sb.AppendLine("- Provide 1-2 paragraphs of analytical insights (100-150 words)");
        sb.AppendLine("- Identify patterns, risks, or notable architectural decisions");
        sb.AppendLine("- Be specific and reference the actual data provided");
        sb.AppendLine("- Use professional technical language");
        sb.AppendLine("- Do not include headers, just the analysis");
        sb.AppendLine();
        sb.AppendLine("Generate the insights now:");

        return sb.ToString();
    }

    private string BuildFindingsPrompt(string reportType, List<string> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert system architect reviewing architectural findings and issues.");
        sb.AppendLine();
        sb.AppendLine($"Report Type: {reportType}");
        sb.AppendLine();
        sb.AppendLine("Findings:");
        foreach (var finding in findings)
        {
            sb.AppendLine($"- {finding}");
        }
        sb.AppendLine();
        sb.AppendLine("Requirements:");
        sb.AppendLine("- Summarize the key themes and priorities (100-150 words)");
        sb.AppendLine("- Group related findings");
        sb.AppendLine("- Suggest high-level remediation strategies");
        sb.AppendLine("- Be direct and actionable");
        sb.AppendLine("- Do not include headers, just the narrative");
        sb.AppendLine();
        sb.AppendLine("Generate the findings summary now:");

        return sb.ToString();
    }

    private string BuildArchitectureAnalysisPrompt(Dictionary<string, object> architectureData)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert enterprise architect performing a comprehensive architecture analysis.");
        sb.AppendLine();
        sb.AppendLine("Architecture Data:");
        sb.AppendLine(JsonSerializer.Serialize(architectureData, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine();
        sb.AppendLine("Requirements:");
        sb.AppendLine("- Analyze service distribution, dependencies, and architectural patterns");
        sb.AppendLine("- Identify strengths and potential areas of concern");
        sb.AppendLine("- Write 2-3 paragraphs (150-200 words)");
        sb.AppendLine("- Be specific using counts and data from the provided information");
        sb.AppendLine("- Use professional technical language");
        sb.AppendLine("- Do not include headers or titles");
        sb.AppendLine();
        sb.AppendLine("Generate the architecture analysis now:");

        return sb.ToString();
    }

    public async Task SavePromptLogAsync(string outputPath)
    {
        var json = JsonSerializer.Serialize(_promptLog, new JsonSerializerOptions 
        { 
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        await File.WriteAllTextAsync(outputPath, json);
    }
}

public class AiPromptLogEntry
{
    public DateTime Timestamp { get; set; }
    public string ReportType { get; set; } = string.Empty;
    public string SectionId { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public string? Error { get; set; }
    public int DurationMs { get; set; }
    public bool Success { get; set; }
}

public class OllamaGenerateResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }
    
    [JsonPropertyName("response")]
    public string? Response { get; set; }
    
    [JsonPropertyName("done")]
    public bool Done { get; set; }
}
