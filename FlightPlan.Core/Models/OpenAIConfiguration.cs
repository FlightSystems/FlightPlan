namespace FlightPlan.Models;

/// <summary>
/// Configuration for OpenAI/Azure OpenAI integration with MCP function calling
/// </summary>
public class OpenAIConfiguration
{
    /// <summary>
    /// OpenAI API endpoint. Use https://api.openai.com/v1 for OpenAI or your Azure OpenAI endpoint
    /// </summary>
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    
    /// <summary>
    /// OpenAI API key or Azure OpenAI API key
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
    
    /// <summary>
    /// Model to use. For OpenAI: gpt-4, gpt-4-turbo, gpt-3.5-turbo. For Azure: your deployment name
    /// </summary>
    public string Model { get; set; } = "gpt-4";
    
    /// <summary>
    /// Whether to use the new MCP function calling mode (true) or legacy RAG pre-injection mode (false)
    /// </summary>
    public bool UseMcpFunctionCalling { get; set; } = false;
    
    /// <summary>
    /// Load configuration from environment variables
    /// OPENAI_ENDPOINT, OPENAI_API_KEY, OPENAI_MODEL, USE_MCP_FUNCTION_CALLING
    /// </summary>
    public static OpenAIConfiguration FromEnvironment()
    {
        var config = new OpenAIConfiguration();
        
        var endpoint = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            config.Endpoint = endpoint;
        }
        
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            config.ApiKey = apiKey;
        }
        
        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        if (!string.IsNullOrWhiteSpace(model))
        {
            config.Model = model;
        }
        
        var useMcp = Environment.GetEnvironmentVariable("USE_MCP_FUNCTION_CALLING");
        if (!string.IsNullOrWhiteSpace(useMcp))
        {
            config.UseMcpFunctionCalling = bool.TryParse(useMcp, out var value) && value;
        }
        
        return config;
    }
    
    /// <summary>
    /// Check if OpenAI is configured and ready to use
    /// </summary>
    public bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Endpoint);
    }
}
