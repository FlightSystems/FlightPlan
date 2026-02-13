using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Azure.AI.OpenAI;
using Azure;
using FlightPlan.Models;

namespace FlightPlan.Services;

/// <summary>
/// Service for executing FlightPlan queries using MCP tools with function calling
/// Instead of pre-injecting RAG and Graph context, this service exposes MCP tools
/// and lets the LLM decide which tools to call
/// </summary>
public class McpQueryService
{
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chatService;
    private readonly string _storeType;

    public McpQueryService(
        string openAiEndpoint,
        string openAiKey,
        string openAiModel = "gpt-4",
        string storeType = "json",
        string? connectionString = null,
        string? indexName = null)
    {
        _storeType = storeType;
        
        // Build Semantic Kernel with OpenAI chat completion
        var builder = Kernel.CreateBuilder();
        
        // Check if this is Azure OpenAI, Ollama, or OpenAI
        if (openAiEndpoint.Contains("openai.azure.com"))
        {
            // Azure OpenAI
            builder.AddAzureOpenAIChatCompletion(
                deploymentName: openAiModel,
                endpoint: openAiEndpoint,
                apiKey: openAiKey);
        }
        else if (openAiEndpoint.Contains("localhost") || openAiEndpoint.Contains("127.0.0.1") || openAiEndpoint.Contains("11434"))
        {
            // Ollama - OpenAI compatible but doesn't require real API key
            // Use a dummy key since Ollama doesn't validate it
#pragma warning disable SKEXP0010 // Type is for evaluation purposes only
            builder.AddOpenAIChatCompletion(
                modelId: openAiModel,
                apiKey: "ollama-key",  // Ollama ignores this
                endpoint: new Uri(openAiEndpoint));
#pragma warning restore SKEXP0010
        }
        else
        {
            // Standard OpenAI
            builder.AddOpenAIChatCompletion(
                modelId: openAiModel,
                apiKey: openAiKey);
        }
        
        _kernel = builder.Build();
        _chatService = _kernel.GetRequiredService<IChatCompletionService>();
        
        // Register MCP tools
        var mcpToolService = new McpToolService(storeType, connectionString, indexName);
        mcpToolService.RegisterTools(_kernel);
    }

    public async Task<QueryResponse> QueryAsync(string query, int? maxTokens = null)
    {
        try
        {
            // Create chat history with system message
            var chatHistory = new ChatHistory();
            chatHistory.AddSystemMessage(BuildSystemPrompt());
            chatHistory.AddUserMessage(query);

            // Configure execution settings for automatic function calling
            var executionSettings = new OpenAIPromptExecutionSettings
            {
                MaxTokens = maxTokens ?? 2000,
                Temperature = 0.3,
                // Enable automatic function calling - the LLM will decide which MCP tools to invoke
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
            };

            // Execute query with automatic function calling
            // The LLM will analyze the query and call the appropriate MCP tools as needed
            var result = await _chatService.GetChatMessageContentAsync(
                chatHistory,
                executionSettings,
                _kernel);

            return new QueryResponse
            {
                Success = true,
                Answer = result.Content ?? "No response generated",
                ChunksFound = 0, // Not applicable with function calling
                TopMatchScore = 0 // Not applicable with function calling
            };
        }
        catch (HttpRequestException ex)
        {
            return new QueryResponse
            {
                Success = false,
                Error = $"Failed to connect to OpenAI: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new QueryResponse
            {
                Success = false,
                Error = $"Query failed: {ex.Message}\n{ex.StackTrace}"
            };
        }
    }

    public async IAsyncEnumerable<string> QueryStreamingAsync(string query, int? maxTokens = null)
    {
        // Note: Streaming with automatic function calling is challenging because:
        // 1. Tools need to execute completely before generating response
        // 2. Semantic Kernel may emit tool call metadata in the stream
        // 
        // For now, we execute the full query and then stream the result word-by-word
        // This provides a streaming-like experience while ensuring tool calls complete
        
        var result = await QueryAsync(query, maxTokens);
        
        if (!result.Success)
        {
            yield return result.Error ?? "Query failed";
            yield break;
        }

        // Stream the response word by word for better UX
        var words = result.Answer.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            yield return words[i];
            if (i < words.Length - 1)
            {
                yield return " ";
            }
            // Small delay to simulate streaming
            await Task.Delay(10);
        }
    }

    private string BuildSystemPrompt()
    {
        var graphTools = _storeType == "falkordb" ? @"
- **query_graph**: Execute Cypher queries for complex graph analysis
- **get_graph_statistics**: Get graph statistics  
- **analyze_service_dependencies**: Analyze service dependencies and dependents
- **get_team_ownership**: Query team ownership" : "";

        return $"""
You are a FlightPlan architecture assistant with access to tools for querying documentation and system architecture.

# Available Tools

You have access to the following tools to answer questions:

- **query_documentation**: Search FlightPlan documentation using semantic search
- **list_documents**: List all available documentation files{graphTools}

# How to Use Tools

1. **Analyze the user's question** to understand what information is needed
2. **Call the appropriate tool(s)** to gather information
3. **Synthesize the results** into a clear, comprehensive answer
4. **Cite your sources** from the tool results

# Important Guidelines

✅ **Always use tools** - Don't rely on general knowledge for FlightPlan-specific questions
✅ **Call multiple tools** if needed - Documentation + Graph queries for comprehensive answers
✅ **Be precise** - Use exact names, IDs, and numbers from tool results
✅ **Cite sources** - Reference which tool provided each piece of information
✅ **Handle missing data** - If tools return no results, explain what's missing

# FlightPlan Concepts

FlightPlan is a system for defining cloud-native architecture:
- **Services**: Microservices/APIs with owners, platforms, dependencies
- **Resources**: Shared infrastructure (databases, queues, storage)
- **Environments**: Deployment stages (dev → QA → UAT → prod)
- **Teams**: Ownership and responsibility
- **Dependencies**: DEPENDS_ON relationships between services/resources

# Response Quality

- Provide direct, actionable answers
- Use tool results as primary evidence
- Structure responses with headers and lists
- Be concise but complete
""";
    }
}
