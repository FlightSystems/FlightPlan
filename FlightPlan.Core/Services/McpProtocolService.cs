using System.Net;
using System.Text;
using System.Text.Json;
using FlightPlan.Models;

namespace FlightPlan.Services;

/// <summary>
/// Service for handling Model Context Protocol (MCP) JSON-RPC requests
/// </summary>
public class McpProtocolService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public delegate Task<object?> McpMethodHandler(object? parameters);
    
    private readonly Dictionary<string, McpMethodHandler> _methodHandlers = new();

    /// <summary>
    /// Register a handler for a specific MCP method
    /// </summary>
    public void RegisterMethod(string method, McpMethodHandler handler)
    {
        _methodHandlers[method] = handler;
    }

    /// <summary>
    /// Handle an incoming MCP request
    /// </summary>
    public async Task HandleMcpRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        response.ContentType = "application/json";

        // Enable CORS for local development
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = 200;
            response.Close();
            return;
        }

        try
        {
            using var reader = new StreamReader(request.InputStream);
            var json = await reader.ReadToEndAsync();
            
            // Log the incoming request for debugging
            Console.WriteLine($"📥 MCP Request: {json}");
            
            var rpcRequest = JsonSerializer.Deserialize<JsonRpcRequest>(json, JsonOptions);

            if (rpcRequest == null)
            {
                SendMcpError(response, -32700, "Parse error", null);
                return;
            }

            // Check if this is a notification (no id)
            var isNotification = rpcRequest.Id == null;

            // Try to handle the method
            if (!_methodHandlers.TryGetValue(rpcRequest.Method, out var handler))
            {
                // For notifications, just log and return
                if (isNotification)
                {
                    Console.WriteLine($"⚠️  Unknown notification: {rpcRequest.Method}");
                    response.StatusCode = 200;
                    response.ContentLength64 = 0;
                    response.Close();
                    return;
                }

                // For requests, send error
                SendMcpError(response, -32601, $"Method not found: {rpcRequest.Method}", rpcRequest.Id);
                return;
            }

            // Execute the handler
            var result = await handler(rpcRequest.Params);

            // For notifications, we don't send a response
            if (isNotification)
            {
                response.StatusCode = 200;
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            // Send successful response
            var rpcResponse = new JsonRpcResponse
            {
                Result = result,
                Id = rpcRequest.Id
            };

            var responseJson = JsonSerializer.Serialize(rpcResponse, JsonOptions);
            var buffer = Encoding.UTF8.GetBytes(responseJson);
            response.StatusCode = 200;
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ MCP Error: {ex.Message}");
            Console.Error.WriteLine($"   Stack: {ex.StackTrace}");
            SendMcpError(response, -32603, $"Internal error: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Send an MCP error response
    /// </summary>
    public static void SendMcpError(HttpListenerResponse response, int code, string message, object? id)
    {
        var rpcResponse = new JsonRpcResponse
        {
            Error = new JsonRpcError
            {
                Code = code,
                Message = message
            },
            Id = id
        };

        var responseJson = JsonSerializer.Serialize(rpcResponse, JsonOptions);
        var buffer = Encoding.UTF8.GetBytes(responseJson);
        response.ContentType = "application/json";
        response.StatusCode = code switch
        {
            -32601 => 404,
            -32700 => 400,
            _ => 500
        };
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    /// <summary>
    /// Deserialize MCP parameters to a specific type
    /// </summary>
    public static T? DeserializeParams<T>(object? paramsObj) where T : class
    {
        if (paramsObj == null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(paramsObj, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    /// <summary>
    /// Create standard MCP initialize response
    /// </summary>
    public static InitializeResult CreateInitializeResult(string serverName, string serverVersion)
    {
        return new InitializeResult
        {
            ProtocolVersion = "2024-11-05",
            ServerInfo = new ServerInfo
            {
                Name = serverName,
                Version = serverVersion
            },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability(),
                Resources = new ResourcesCapability(),
                Prompts = new PromptsCapability()
            }
        };
    }

    /// <summary>
    /// Create a successful tool call result
    /// </summary>
    public static CallToolResult CreateToolResult(string text, bool isError = false)
    {
        return new CallToolResult
        {
            IsError = isError,
            Content = new List<ToolContent>
            {
                new ToolContent { Text = text }
            }
        };
    }

    /// <summary>
    /// Create an error tool call result
    /// </summary>
    public static CallToolResult CreateToolError(string errorMessage)
    {
        return CreateToolResult(errorMessage, isError: true);
    }
}
