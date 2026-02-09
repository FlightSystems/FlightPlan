using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlightPlan.Commands;

internal static class ServeCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly string MermaidScriptTemplate = ReadEmbeddedResource("Flight.Resources.mermaid-init.html");
    private static readonly string StylesTemplate = ReadEmbeddedResource("Flight.Resources.serve-styles.css");
    private static readonly string ChatScriptTemplate = ReadEmbeddedResource("Flight.Resources.chat-interface.js");

    internal static void Configure(
        Command serveCmd,
        Argument<DirectoryInfo> dirArg,
        Option<int> portOpt,
        Option<bool> openOpt)
    {
        serveCmd.SetHandler((InvocationContext ctx) =>
        {
            var dir = ctx.ParseResult.GetValueForArgument(dirArg);
            var port = ctx.ParseResult.GetValueForOption(portOpt);
            var open = ctx.ParseResult.GetValueForOption(openOpt);

            if (!dir.Exists)
            {
                Console.Error.WriteLine($"❌ Directory not found: {dir.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            Execute(dir, port, open);
        });
    }

    private static void Execute(DirectoryInfo dir, int port, bool open)
    {
        Console.WriteLine($"🚀 Starting Flight documentation server...");
        Console.WriteLine($"📁 Serving from: {dir.FullName}");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop the server.");
        Console.WriteLine();

        using var server = new HttpServerService(port);
        using var cts = new CancellationTokenSource();
        
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n🛑 Shutting down server...");
            cts.Cancel();
            server.Stop();
        };

        server.HandleRequest = async context => await HandleRequest(context, dir);
        
        try
        {
            server.StartAsync(open, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"❌ Server error: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task HandleRequest(HttpListenerContext context, DirectoryInfo baseDir)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "/";

        // Handle API endpoints
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleApiRequest(context, path);
            return;
        }
        
        var fileService = new StaticFileService(baseDir);
        
        // Resolve and validate file path
        if (!fileService.TryResolvePath(path, out var fullPath))
        {
            StaticFileService.Send404(response, "Invalid path");
            HttpServerService.LogRequest(request, 404);
            return;
        }

        // Check if path is a directory
        if (Directory.Exists(fullPath))
        {
            // Try to find index file
            if (StaticFileService.TryFindIndexFile(fullPath, out var indexFile))
            {
                var redirectPath = path.TrimEnd('/') + "/" + indexFile;
                HttpServerService.SendRedirect(response, redirectPath);
                HttpServerService.LogRequest(request, 302);
                return;
            }
            
            StaticFileService.Send404(response, "Directory listing not available");
            HttpServerService.LogRequest(request, 404);
            return;
        }

        // Check if file exists
        if (!File.Exists(fullPath))
        {
            StaticFileService.Send404(response, "File not found");
            HttpServerService.LogRequest(request, 404);
            return;
        }

        // Determine how to serve file
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        
        try
        {
            if (extension is ".md" or ".markdown")
            {
                ServeMarkdownAsHtml(fullPath, response, baseDir);
            }
            else
            {
                fileService.ServeFile(fullPath, response);
            }
            HttpServerService.LogRequest(request, 200);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ Error serving {path}: {ex.Message}");
            StaticFileService.Send500(response, ex.Message);
            HttpServerService.LogRequest(request, 500);
        }
    }

    private static async Task HandleApiRequest(HttpListenerContext context, string path)
    {
        var request = context.Request;
        var response = context.Response;

        // Enable CORS for local development
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (request.HttpMethod == "OPTIONS")
        {
            response.StatusCode = 200;
            response.Close();
            HttpServerService.LogRequest(request, 200);
            return;
        }

        if (path == "/api/query" && request.HttpMethod == "POST")
        {
            await HandleQueryApi(context);
        }
        else if (path == "/api/query/stream" && request.HttpMethod == "POST")
        {
            await HandleQueryStreamApi(context);
        }
        else if (path == "/api/mcp" && request.HttpMethod == "POST")
        {
            var mcpService = CreateMcpService();
            await mcpService.HandleMcpRequest(context);
            HttpServerService.LogRequest(request, 200);
        }
        else
        {
            response.StatusCode = 404;
            response.ContentType = "application/json";
            var errorJson = "{\"error\":\"API endpoint not found\"}";
            var buffer = Encoding.UTF8.GetBytes(errorJson);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
            HttpServerService.LogRequest(request, 404);
        }
    }

    private static async Task HandleQueryApi(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            using var reader = new StreamReader(request.InputStream);
            var json = await reader.ReadToEndAsync();
            var queryRequest = JsonSerializer.Deserialize<QueryRequest>(json, JsonOptions);

            if (queryRequest == null || string.IsNullOrWhiteSpace(queryRequest.Query))
            {
                response.StatusCode = 400;
                var errorJson = "{\"error\":\"Query is required\"}";
                var buffer = Encoding.UTF8.GetBytes(errorJson);
                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
                HttpServerService.LogRequest(request, 400);
                return;
            }

            var queryService = new QueryService();
            // Use root-relative URLs for local serving instead of GitHub URLs
            var result = await queryService.QueryAsync(queryRequest.Query, baseUrlOverride: "/");

            response.ContentType = "application/json";
            response.StatusCode = 200;
            var resultJson = JsonSerializer.Serialize(result, JsonOptions);
            var resultBuffer = Encoding.UTF8.GetBytes(resultJson);
            response.ContentLength64 = resultBuffer.Length;
            response.OutputStream.Write(resultBuffer, 0, resultBuffer.Length);
            response.Close();
            HttpServerService.LogRequest(request, 200);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ API Error: {ex.Message}");
            response.StatusCode = 500;
            var errorJson = $"{{\"error\":\"{WebUtility.HtmlEncode(ex.Message)}\"}}";
            var buffer = Encoding.UTF8.GetBytes(errorJson);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
            HttpServerService.LogRequest(request, 500);
        }
    }

    private static async Task HandleQueryStreamApi(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            using var reader = new StreamReader(request.InputStream);
            var json = await reader.ReadToEndAsync();
            var queryRequest = JsonSerializer.Deserialize<QueryRequest>(json, JsonOptions);

            if (queryRequest == null || string.IsNullOrWhiteSpace(queryRequest.Query))
            {
                response.StatusCode = 400;
                response.Close();
                HttpServerService.LogRequest(request, 400);
                return;
            }

            response.ContentType = "text/event-stream";
            response.Headers.Add("Cache-Control", "no-cache");
            response.StatusCode = 200;

            var queryService = new QueryService();
            // Use root-relative URLs for local serving instead of GitHub URLs
            await foreach (var token in queryService.QueryStreamingAsync(queryRequest.Query, baseUrlOverride: "/"))
            {
                var eventData = $"data: {JsonSerializer.Serialize(new { token }, JsonOptions)}\n\n";
                var buffer = Encoding.UTF8.GetBytes(eventData);
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Flush();
            }

            // Send done event
            var doneData = "data: {\"done\":true}\n\n";
            var doneBuffer = Encoding.UTF8.GetBytes(doneData);
            response.OutputStream.Write(doneBuffer, 0, doneBuffer.Length);
            response.OutputStream.Flush();
            response.Close();
            HttpServerService.LogRequest(request, 200);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ Streaming API Error: {ex.Message}");
            response.StatusCode = 500;
            response.Close();
            HttpServerService.LogRequest(request, 500);
        }
    }

    /// <summary>
    /// Create and configure the MCP service with all method handlers
    /// </summary>
    private static McpProtocolService CreateMcpService()
    {
        var mcpService = new McpProtocolService();
        
        // Register MCP method handlers
        mcpService.RegisterMethod("initialize", async (params_) => 
            await Task.FromResult(McpProtocolService.CreateInitializeResult(
                "FlightPlan Documentation Server", 
                "1.0.0"
            ))
        );
        
        mcpService.RegisterMethod("initialized", (params_) =>
        {
            Console.WriteLine("✅ MCP client initialized");
            return Task.FromResult<object?>(new { });
        });
        
        mcpService.RegisterMethod("notifications/initialized", (params_) =>
        {
            Console.WriteLine("✅ MCP client initialized");
            return Task.FromResult<object?>(new { });
        });
        
        mcpService.RegisterMethod("tools/list", async (params_) => await HandleMcpToolsList());
        mcpService.RegisterMethod("tools/call", async (params_) => await HandleMcpToolsCall(params_));
        mcpService.RegisterMethod("resources/list", async (params_) => await HandleMcpResourcesList());
        mcpService.RegisterMethod("resources/read", async (params_) => await HandleMcpResourcesRead(params_));
        mcpService.RegisterMethod("prompts/list", async (params_) => await HandleMcpPromptsList());
        mcpService.RegisterMethod("prompts/get", async (params_) => await HandleMcpPromptsGet(params_));
        
        return mcpService;
    }

    private static Task<ListToolsResult> HandleMcpToolsList()
    {
        var tools = new List<McpTool>
        {
            new McpTool
            {
                Name = "query_documentation",
                Description = "Search and query the FlightPlan documentation using AI-powered semantic search. Returns relevant documentation with citations.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            description = "The question or search query about the FlightPlan architecture and documentation"
                        }
                    },
                    required = new[] { "query" }
                }
            },
            new McpTool
            {
                Name = "list_documents",
                Description = "List all available markdown documentation files in the FlightPlan documentation system.",
                InputSchema = new
                {
                    type = "object",
                    properties = new { }
                }
            }
        };

        return Task.FromResult(new ListToolsResult { Tools = tools });
    }

    private static async Task<CallToolResult> HandleMcpToolsCall(object? paramsObj)
    {
        var callParams = McpProtocolService.DeserializeParams<CallToolParams>(paramsObj);

        if (callParams == null || string.IsNullOrWhiteSpace(callParams.Name))
        {
            return McpProtocolService.CreateToolError("Invalid tool call parameters");
        }

        return callParams.Name switch
        {
            "query_documentation" => await HandleQueryDocumentationTool(callParams.Arguments),
            "list_documents" => await HandleListDocumentsTool(),
            _ => McpProtocolService.CreateToolError($"Unknown tool: {callParams.Name}")
        };
    }

    private static async Task<CallToolResult> HandleQueryDocumentationTool(Dictionary<string, object>? arguments)
    {
        if (arguments == null || !arguments.TryGetValue("query", out var queryObj))
        {
            return McpProtocolService.CreateToolError("Missing 'query' parameter");
        }

        var query = queryObj?.ToString();
        if (string.IsNullOrWhiteSpace(query))
        {
            return McpProtocolService.CreateToolError("Query cannot be empty");
        }

        try
        {
            var queryService = new QueryService();
            var result = await queryService.QueryAsync(query);

            return McpProtocolService.CreateToolResult(result.Answer ?? "No response generated");
        }
        catch (Exception ex)
        {
            return McpProtocolService.CreateToolError($"Error querying documentation: {ex.Message}");
        }
    }

    private static Task<CallToolResult> HandleListDocumentsTool()
    {
        try
        {
            var docsPath = Path.Combine(Directory.GetCurrentDirectory(), "../docs/flightplan");
            if (!Directory.Exists(docsPath))
            {
                docsPath = Path.Combine(Directory.GetCurrentDirectory(), "docs/flightplan");
            }

            if (!Directory.Exists(docsPath))
            {
                return Task.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = new List<ToolContent>
                    {
                        new ToolContent { Text = "Documentation directory not found" }
                    }
                });
            }

            var mdFiles = Directory.GetFiles(docsPath, "*.md", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(docsPath, f))
                .OrderBy(f => f)
                .ToList();

            var fileList = string.Join("\n", mdFiles.Select(f => $"- {f}"));

            return Task.FromResult(new CallToolResult
            {
                Content = new List<ToolContent>
                {
                    new ToolContent { Text = $"Available documentation files:\n\n{fileList}" }
                }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CallToolResult
            {
                IsError = true,
                Content = new List<ToolContent>
                {
                    new ToolContent { Text = $"Error listing documents: {ex.Message}" }
                }
            });
        }
    }

    private static Task<ListResourcesResult> HandleMcpResourcesList()
    {
        try
        {
            var docsPath = Path.Combine(Directory.GetCurrentDirectory(), "../docs/flightplan");
            if (!Directory.Exists(docsPath))
            {
                docsPath = Path.Combine(Directory.GetCurrentDirectory(), "docs/flightplan");
            }

            if (!Directory.Exists(docsPath))
            {
                return Task.FromResult(new ListResourcesResult());
            }

            var mdFiles = Directory.GetFiles(docsPath, "*.md", SearchOption.AllDirectories);
            var resources = mdFiles.Select(filePath =>
            {
                var relativePath = Path.GetRelativePath(docsPath, filePath);
                var uri = $"file:///{relativePath.Replace(Path.DirectorySeparatorChar, '/')}";
                var name = Path.GetFileName(filePath);

                return new McpResource
                {
                    Uri = uri,
                    Name = name,
                    Description = $"FlightPlan documentation: {relativePath}",
                    MimeType = "text/markdown"
                };
            }).ToList();

            return Task.FromResult(new ListResourcesResult { Resources = resources });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error listing resources: {ex.Message}");
            return Task.FromResult(new ListResourcesResult());
        }
    }

    private static Task<ReadResourceResult> HandleMcpResourcesRead(object? paramsObj)
    {
        var readParams = McpProtocolService.DeserializeParams<ReadResourceParams>(paramsObj);

        if (readParams == null || string.IsNullOrWhiteSpace(readParams.Uri))
        {
            return Task.FromResult(new ReadResourceResult());
        }

        try
        {
            // Extract file path from URI (file:///path/to/file.md)
            var uri = readParams.Uri;
            var filePath = uri.StartsWith("file:///") 
                ? uri.Substring(8) 
                : uri;

            var docsPath = Path.Combine(Directory.GetCurrentDirectory(), "../docs/flightplan");
            if (!Directory.Exists(docsPath))
            {
                docsPath = Path.Combine(Directory.GetCurrentDirectory(), "docs/flightplan");
            }

            var fullPath = Path.Combine(docsPath, filePath);

            if (!File.Exists(fullPath))
            {
                return Task.FromResult(new ReadResourceResult());
            }

            var content = File.ReadAllText(fullPath);

            return Task.FromResult(new ReadResourceResult
            {
                Contents = new List<ResourceContent>
                {
                    new ResourceContent
                    {
                        Uri = readParams.Uri,
                        MimeType = "text/markdown",
                        Text = content
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error reading resource: {ex.Message}");
            return Task.FromResult(new ReadResourceResult());
        }
    }

    private static Task<ListPromptsResult> HandleMcpPromptsList()
    {
        var prompts = new List<McpPrompt>
        {
            new McpPrompt
            {
                Name = "explain_architecture",
                Description = "Get a comprehensive explanation of the FlightPlan system architecture"
            },
            new McpPrompt
            {
                Name = "list_services",
                Description = "List all services in the FlightPlan system catalog"
            },
            new McpPrompt
            {
                Name = "deployment_overview",
                Description = "Get an overview of the deployment strategy and environments"
            },
            new McpPrompt
            {
                Name = "security_summary",
                Description = "Get a summary of the security architecture and policies"
            },
            new McpPrompt
            {
                Name = "onboarding_guide",
                Description = "Get the developer onboarding guide for the FlightPlan system"
            },
            new McpPrompt
            {
                Name = "ask_custom",
                Description = "Ask a custom question about the FlightPlan documentation",
                Arguments = new List<PromptArgument>
                {
                    new PromptArgument
                    {
                        Name = "question",
                        Description = "Your custom question about the documentation",
                        Required = true
                    }
                }
            }
        };

        return Task.FromResult(new ListPromptsResult { Prompts = prompts });
    }

    private static Task<GetPromptResult> HandleMcpPromptsGet(object? paramsObj)
    {
        var promptParams = McpProtocolService.DeserializeParams<GetPromptParams>(paramsObj);

        if (promptParams == null || string.IsNullOrWhiteSpace(promptParams.Name))
        {
            return Task.FromResult(new GetPromptResult
            {
                Description = "Missing prompt name"
            });
        }

        var promptText = promptParams.Name switch
        {
            "explain_architecture" => "Explain the overall architecture of the FlightPlan system. Include information about the system design, key components, technology stack, and how different services interact with each other.",
            "list_services" => "List all the services in the FlightPlan system. For each service, provide its name, purpose, and key responsibilities.",
            "deployment_overview" => "Provide an overview of the deployment strategy for the FlightPlan system. Include information about environments, deployment processes, and infrastructure.",
            "security_summary" => "Summarize the security architecture and policies of the FlightPlan system. Include authentication, authorization, data protection, and compliance considerations.",
            "onboarding_guide" => "Provide a comprehensive developer onboarding guide for the FlightPlan system. Include setup instructions, development workflow, and key resources.",
            "ask_custom" => promptParams.Arguments?.TryGetValue("question", out var question) == true && !string.IsNullOrWhiteSpace(question)
                ? question
                : "Please provide a question in the 'question' argument.",
            _ => $"Unknown prompt: {promptParams.Name}"
        };

        return Task.FromResult(new GetPromptResult
        {
            Description = $"FlightPlan documentation query: {promptParams.Name}",
            Messages = new List<PromptMessage>
            {
                new PromptMessage
                {
                    Role = "user",
                    Content = new PromptContent
                    {
                        Type = "text",
                        Text = promptText
                    }
                }
            }
        });
    }

    private class QueryRequest
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;
    }

    private static void ServeMarkdownAsHtml(string filePath, HttpListenerResponse response, DirectoryInfo baseDir)
    {
        var markdown = File.ReadAllText(filePath);
        var renderer = new MarkdownRenderingService();
        var htmlContent = renderer.RenderToHtml(markdown);
        
        var relativePath = Path.GetRelativePath(baseDir.FullName, filePath);
        var title = Path.GetFileNameWithoutExtension(filePath);
        
        // Build complete HTML page
        var html = renderer.BuildHtmlPage(
            htmlContent,
            title,
            relativePath,
            mermaidScript: MermaidScriptTemplate,
            stylesTemplate: StylesTemplate,
            chatScript: ChatScriptTemplate,
            includeChatInterface: true
        );

        var buffer = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.OutputStream.Close();
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        var assembly = typeof(ServeCommand).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            var available = assembly.GetManifestResourceNames();
            var availableMsg = available.Length == 0 ? "(none)" : string.Join(", ", available);
            throw new InvalidOperationException(
                $"Embedded resource not found: '{resourceName}'. Available resources: {availableMsg}");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
