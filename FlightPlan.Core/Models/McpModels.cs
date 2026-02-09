namespace FlightPlan.Models;

/// <summary>
/// JSON-RPC 2.0 request structure for MCP
/// </summary>
public class JsonRpcRequest
{
    public string Jsonrpc { get; set; } = "2.0";
    public string Method { get; set; } = string.Empty;
    public object? Params { get; set; }
    public object? Id { get; set; }
}

/// <summary>
/// JSON-RPC 2.0 response structure for MCP
/// </summary>
public class JsonRpcResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public object? Result { get; set; }
    public JsonRpcError? Error { get; set; }
    public object? Id { get; set; }
}

/// <summary>
/// JSON-RPC 2.0 error structure
/// </summary>
public class JsonRpcError
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
}

/// <summary>
/// MCP Server initialization parameters
/// </summary>
public class InitializeParams
{
    public string ProtocolVersion { get; set; } = string.Empty;
    public ClientInfo? ClientInfo { get; set; }
    public ServerCapabilities? Capabilities { get; set; }
}

/// <summary>
/// MCP Client information
/// </summary>
public class ClientInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// MCP Server capabilities
/// </summary>
public class ServerCapabilities
{
    public ToolsCapability? Tools { get; set; }
    public ResourcesCapability? Resources { get; set; }
    public PromptsCapability? Prompts { get; set; }
}

public class ToolsCapability
{
    public bool? ListChanged { get; set; }
}

public class ResourcesCapability
{
    public bool? Subscribe { get; set; }
    public bool? ListChanged { get; set; }
}

public class PromptsCapability
{
    public bool? ListChanged { get; set; }
}

/// <summary>
/// MCP Server initialization result
/// </summary>
public class InitializeResult
{
    public string ProtocolVersion { get; set; } = "2024-11-05";
    public ServerCapabilities Capabilities { get; set; } = new();
    public ServerInfo ServerInfo { get; set; } = new();
}

/// <summary>
/// MCP Server information
/// </summary>
public class ServerInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// MCP Tool definition
/// </summary>
public class McpTool
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public object InputSchema { get; set; } = new { };
}

/// <summary>
/// MCP Tool call parameters
/// </summary>
public class CallToolParams
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, object>? Arguments { get; set; }
}

/// <summary>
/// MCP Tool response
/// </summary>
public class CallToolResult
{
    public List<ToolContent> Content { get; set; } = new();
    public bool? IsError { get; set; }
}

/// <summary>
/// MCP Tool content item
/// </summary>
public class ToolContent
{
    public string Type { get; set; } = "text";
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// MCP Resource definition
/// </summary>
public class McpResource
{
    public string Uri { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? MimeType { get; set; }
}

/// <summary>
/// MCP Resource read parameters
/// </summary>
public class ReadResourceParams
{
    public string Uri { get; set; } = string.Empty;
}

/// <summary>
/// MCP Resource read result
/// </summary>
public class ReadResourceResult
{
    public List<ResourceContent> Contents { get; set; } = new();
}

/// <summary>
/// MCP Resource content item
/// </summary>
public class ResourceContent
{
    public string Uri { get; set; } = string.Empty;
    public string MimeType { get; set; } = "text/plain";
    public string? Text { get; set; }
}

/// <summary>
/// MCP Prompt definition
/// </summary>
public class McpPrompt
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<PromptArgument>? Arguments { get; set; }
}

/// <summary>
/// MCP Prompt argument
/// </summary>
public class PromptArgument
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool? Required { get; set; }
}

/// <summary>
/// MCP Get prompt parameters
/// </summary>
public class GetPromptParams
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string>? Arguments { get; set; }
}

/// <summary>
/// MCP Get prompt result
/// </summary>
public class GetPromptResult
{
    public string? Description { get; set; }
    public List<PromptMessage> Messages { get; set; } = new();
}

/// <summary>
/// MCP Prompt message
/// </summary>
public class PromptMessage
{
    public string Role { get; set; } = "user";
    public PromptContent Content { get; set; } = new();
}

/// <summary>
/// MCP Prompt content
/// </summary>
public class PromptContent
{
    public string Type { get; set; } = "text";
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// List tools result
/// </summary>
public class ListToolsResult
{
    public List<McpTool> Tools { get; set; } = new();
}

/// <summary>
/// List resources result
/// </summary>
public class ListResourcesResult
{
    public List<McpResource> Resources { get; set; } = new();
}

/// <summary>
/// List prompts result
/// </summary>
public class ListPromptsResult
{
    public List<McpPrompt> Prompts { get; set; } = new();
}
