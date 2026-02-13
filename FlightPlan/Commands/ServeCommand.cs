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

    // Store configuration for API handlers
    private static string? _storeType;
    private static string? _connectionString;
    private static string? _indexName;
    private static OpenAIConfiguration? _openAIConfig;

    internal static void Configure(
        Command serveCmd,
        Argument<DirectoryInfo> dirArg,
        Option<int> portOpt,
        Option<bool> openOpt,
        Option<string> storeTypeOpt,
        Option<string?> connectionStringOpt,
        Option<string?> indexNameOpt)
    {
        serveCmd.SetHandler((InvocationContext ctx) =>
        {
            var dir = ctx.ParseResult.GetValueForArgument(dirArg);
            var port = ctx.ParseResult.GetValueForOption(portOpt);
            var open = ctx.ParseResult.GetValueForOption(openOpt);
            var storeType = ctx.ParseResult.GetValueForOption(storeTypeOpt) ?? "json";
            var connectionString = ctx.ParseResult.GetValueForOption(connectionStringOpt);
            var indexName = ctx.ParseResult.GetValueForOption(indexNameOpt);

            if (!dir.Exists)
            {
                Console.Error.WriteLine($"❌ Directory not found: {dir.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            Execute(dir, port, open, storeType, connectionString, indexName);
        });
    }

    private static void Execute(DirectoryInfo dir, int port, bool open, string storeType, string? connectionString, string? indexName)
    {
        // Store configuration for API handlers
        _storeType = storeType;
        _connectionString = connectionString;
        _indexName = indexName;
        
        // Load OpenAI configuration from environment
        _openAIConfig = OpenAIConfiguration.FromEnvironment();

        Console.WriteLine($"🚀 Starting Flight documentation server...");
        Console.WriteLine($"📁 Serving from: {dir.FullName}");
        if (storeType != "json")
        {
            Console.WriteLine($"🗄️  Vector store: {storeType.ToUpper()}");
            if (!string.IsNullOrEmpty(indexName))
            {
                Console.WriteLine($"📇 Index name: {indexName}");
            }
        }
        
        // Display MCP function calling status
        if (_openAIConfig?.IsConfigured() == true)
        {
            Console.WriteLine($"🤖 AI Mode: {(_openAIConfig.UseMcpFunctionCalling ? "MCP Function Calling" : "Legacy RAG Pre-injection")}");
            Console.WriteLine($"🔗 OpenAI: {(_openAIConfig.Endpoint.Contains("azure") ? "Azure OpenAI" : "OpenAI")} ({_openAIConfig.Model})");
        }
        else
        {
            Console.WriteLine($"🤖 AI Mode: Ollama (Legacy RAG Pre-injection)");
        }
        
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
        else if (path == "/api/graph/query" && request.HttpMethod == "POST")
        {
            await HandleGraphQueryApi(context);
        }
        else if (path == "/api/graph/statistics" && request.HttpMethod == "GET")
        {
            await HandleGraphStatisticsApi(context);
        }
        else if (path.StartsWith("/api/graph/service/") && request.HttpMethod == "GET")
        {
            await HandleGraphServiceApi(context, path);
        }
        else if (path.StartsWith("/api/graph/team/") && request.HttpMethod == "GET")
        {
            await HandleGraphTeamApi(context, path);
        }
        else if (path == "/api/graph/patterns" && request.HttpMethod == "GET")
        {
            await HandleGraphPatternsApi(context);
        }
        else if (path == "/api/graph/enabled" && request.HttpMethod == "GET")
        {
            await HandleGraphEnabledApi(context);
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

            QueryResponse result;
            
            // Check if OpenAI is configured and MCP function calling is enabled
            if (_openAIConfig?.IsConfigured() == true && _openAIConfig.UseMcpFunctionCalling)
            {
                // Use new MCP function calling mode
                var queryService = new McpQueryService(
                    openAiEndpoint: _openAIConfig.Endpoint,
                    openAiKey: _openAIConfig.ApiKey,
                    openAiModel: _openAIConfig.Model,
                    storeType: _storeType ?? "json",
                    connectionString: _connectionString,
                    indexName: _indexName);
                    
                result = await queryService.QueryAsync(queryRequest.Query);
            }
            else
            {
                // Use legacy RAG pre-injection mode
                var queryService = new QueryService(
                    storeType: _storeType ?? "json",
                    connectionString: _connectionString,
                    indexName: _indexName);
                    
                // Use root-relative URLs for local serving instead of GitHub URLs
                result = await queryService.QueryAsync(queryRequest.Query, baseUrlOverride: "/");
            }

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

            // Check if OpenAI is configured and MCP function calling is enabled
            if (_openAIConfig?.IsConfigured() == true && _openAIConfig.UseMcpFunctionCalling)
            {
                // Use new MCP function calling mode
                var queryService = new McpQueryService(
                    openAiEndpoint: _openAIConfig.Endpoint,
                    openAiKey: _openAIConfig.ApiKey,
                    openAiModel: _openAIConfig.Model,
                    storeType: _storeType ?? "json",
                    connectionString: _connectionString,
                    indexName: _indexName);
                    
                await foreach (var token in queryService.QueryStreamingAsync(queryRequest.Query))
                {
                    var eventData = $"data: {JsonSerializer.Serialize(new { token }, JsonOptions)}\n\n";
                    var buffer = Encoding.UTF8.GetBytes(eventData);
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                    response.OutputStream.Flush();
                }
            }
            else
            {
                // Use legacy RAG pre-injection mode
                var queryService = new QueryService(
                    storeType: _storeType ?? "json",
                    connectionString: _connectionString,
                    indexName: _indexName);
                    
                // Use root-relative URLs for local serving instead of GitHub URLs
                await foreach (var token in queryService.QueryStreamingAsync(queryRequest.Query, baseUrlOverride: "/"))
                {
                    var eventData = $"data: {JsonSerializer.Serialize(new { token }, JsonOptions)}\n\n";
                    var buffer = Encoding.UTF8.GetBytes(eventData);
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                    response.OutputStream.Flush();
                }
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

        // Add graph query tools if FalkorDB is enabled
        if (_storeType == "falkordb")
        {
            tools.Add(new McpTool
            {
                Name = "query_graph",
                Description = "Execute a Cypher query against the FlightPlan graph database to analyze services, dependencies, teams, and resources. Use this for complex queries about system architecture and relationships.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            description = "Cypher query to execute (e.g., MATCH (s:Service) RETURN s.name)"
                        },
                        parameters = new
                        {
                            type = "object",
                            description = "Optional parameters for the query (use $paramName in query)"
                        }
                    },
                    required = new[] { "query" }
                }
            });

            tools.Add(new McpTool
            {
                Name = "get_graph_statistics",
                Description = "Get statistics about the FlightPlan graph including node counts and relationship counts by type.",
                InputSchema = new
                {
                    type = "object",
                    properties = new { }
                }
            });

            tools.Add(new McpTool
            {
                Name = "analyze_service_dependencies",
                Description = "Analyze dependencies for a specific service or resource, including what it depends on and what depends on it. Works with both services and resources (databases, messaging systems, etc.).",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        serviceId = new
                        {
                            type = "string",
                            description = "The service or resource ID to analyze (e.g., 'web-app-services-rest-claim-api' or 'data/xeodesigner')"
                        },
                        includeImpact = new
                        {
                            type = "boolean",
                            description = "Include impact analysis (all services that would be affected)"
                        }
                    },
                    required = new[] { "serviceId" }
                }
            });

            tools.Add(new McpTool
            {
                Name = "get_team_ownership",
                Description = "Get all services and resources owned by a specific team.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        teamId = new
                        {
                            type = "string",
                            description = "The team ID to query"
                        }
                    },
                    required = new[] { "teamId" }
                }
            });
        }

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
            "query_graph" => await HandleQueryGraphTool(callParams.Arguments),
            "get_graph_statistics" => await HandleGraphStatisticsTool(),
            "analyze_service_dependencies" => await HandleServiceDependenciesTool(callParams.Arguments),
            "get_team_ownership" => await HandleTeamOwnershipTool(callParams.Arguments),
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
            var queryService = new QueryService(
                storeType: _storeType ?? "json",
                connectionString: _connectionString,
                indexName: _indexName);
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

    // ============================================================
    // Graph Query API Handlers
    // ============================================================

    private static async Task HandleGraphEnabledApi(HttpListenerContext context)
    {
        var response = context.Response;
        response.ContentType = "application/json";
        response.StatusCode = 200;
        
        var enabled = _storeType == "falkordb";
        var resultJson = JsonSerializer.Serialize(new { enabled, storeType = _storeType, indexName = _indexName }, JsonOptions);
        var buffer = Encoding.UTF8.GetBytes(resultJson);
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
        HttpServerService.LogRequest(context.Request, 200);
    }

    private static async Task HandleGraphQueryApi(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (_storeType != "falkordb")
        {
            response.StatusCode = 400;
            response.ContentType = "application/json";
            var errorJson = "{\"error\":\"Graph queries require FalkorDB store\"}";
            var buffer = Encoding.UTF8.GetBytes(errorJson);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
            HttpServerService.LogRequest(request, 400);
            return;
        }

        try
        {
            using var reader = new StreamReader(request.InputStream);
            var json = await reader.ReadToEndAsync();
            var queryRequest = JsonSerializer.Deserialize<GraphQueryRequest>(json, JsonOptions);

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

            var store = VectorStoreFactory.Create(_storeType, _connectionString, _indexName) as FalkorDBVectorStore;
            if (store == null)
            {
                throw new InvalidOperationException("Failed to create FalkorDB store");
            }

            var result = await store.ExecuteCypherQuery(queryRequest.Query, queryRequest.Parameters);

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
            Console.Error.WriteLine($"❌ Graph Query API Error: {ex.Message}");
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

    private static async Task HandleGraphStatisticsApi(HttpListenerContext context)
    {
        var response = context.Response;

        if (_storeType != "falkordb")
        {
            response.StatusCode = 400;
            response.ContentType = "application/json";
            var errorJson = "{\"error\":\"Graph statistics require FalkorDB store\"}";
            var buffer = Encoding.UTF8.GetBytes(errorJson);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
            HttpServerService.LogRequest(context.Request, 400);
            return;
        }

        try
        {
            var store = VectorStoreFactory.Create(_storeType, _connectionString, _indexName) as FalkorDBVectorStore;
            if (store == null)
            {
                throw new InvalidOperationException("Failed to create FalkorDB store");
            }

            var stats = await store.GetGraphStatistics();

            response.ContentType = "application/json";
            response.StatusCode = 200;
            var resultJson = JsonSerializer.Serialize(stats, JsonOptions);
            var resultBuffer = Encoding.UTF8.GetBytes(resultJson);
            response.ContentLength64 = resultBuffer.Length;
            response.OutputStream.Write(resultBuffer, 0, resultBuffer.Length);
            response.Close();
            HttpServerService.LogRequest(context.Request, 200);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ Graph Statistics API Error: {ex.Message}");
            response.StatusCode = 500;
            var errorJson = $"{{\"error\":\"{WebUtility.HtmlEncode(ex.Message)}\"}}";
            var buffer = Encoding.UTF8.GetBytes(errorJson);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
            HttpServerService.LogRequest(context.Request, 500);
        }
    }

    private static async Task HandleGraphServiceApi(HttpListenerContext context, string path)
    {
        var response = context.Response;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        if (segments.Length < 3)
        {
            response.StatusCode = 400;
            response.Close();
            HttpServerService.LogRequest(context.Request, 400);
            return;
        }

        var serviceId = WebUtility.UrlDecode(segments[2]);
        var action = segments.Length > 3 ? segments[3] : "info";

        try
        {
            var store = VectorStoreFactory.Create(_storeType ?? "json", _connectionString, _indexName) as FalkorDBVectorStore;
            if (store == null)
            {
                throw new InvalidOperationException("FalkorDB store required");
            }

            object result = action switch
            {
                "dependencies" => await store.GetServiceDependencies(serviceId),
                "dependents" => await store.GetServiceDependents(serviceId),
                "impact" => await store.GetImpactAnalysis(serviceId),
                _ => new { error = "Unknown action" }
            };

            response.ContentType = "application/json";
            response.StatusCode = 200;
            var resultJson = JsonSerializer.Serialize(result, JsonOptions);
            var resultBuffer = Encoding.UTF8.GetBytes(resultJson);
            response.ContentLength64 = resultBuffer.Length;
            response.OutputStream.Write(resultBuffer, 0, resultBuffer.Length);
            response.Close();
            HttpServerService.LogRequest(context.Request, 200);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ Graph Service API Error: {ex.Message}");
            response.StatusCode = 500;
            var errorJson = $"{{\"error\":\"{WebUtility.HtmlEncode(ex.Message)}\"}}";
            var buffer = Encoding.UTF8.GetBytes(errorJson);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
            HttpServerService.LogRequest(context.Request, 500);
        }
    }

    private static async Task HandleGraphTeamApi(HttpListenerContext context, string path)
    {
        var response = context.Response;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        if (segments.Length < 3)
        {
            response.StatusCode = 400;
            response.Close();
            HttpServerService.LogRequest(context.Request, 400);
            return;
        }

        var teamId = WebUtility.UrlDecode(segments[2]);

        try
        {
            var store = VectorStoreFactory.Create(_storeType ?? "json", _connectionString, _indexName) as FalkorDBVectorStore;
            if (store == null)
            {
                throw new InvalidOperationException("FalkorDB store required");
            }

            var ownership = await store.GetTeamOwnership(teamId);

            response.ContentType = "application/json";
            response.StatusCode = 200;
            var resultJson = JsonSerializer.Serialize(ownership, JsonOptions);
            var resultBuffer = Encoding.UTF8.GetBytes(resultJson);
            response.ContentLength64 = resultBuffer.Length;
            response.OutputStream.Write(resultBuffer, 0, resultBuffer.Length);
            response.Close();
            HttpServerService.LogRequest(context.Request, 200);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ Graph Team API Error: {ex.Message}");
            response.StatusCode = 500;
            var errorJson = $"{{\"error\":\"{WebUtility.HtmlEncode(ex.Message)}\"}}";
            var buffer = Encoding.UTF8.GetBytes(errorJson);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
            HttpServerService.LogRequest(context.Request, 500);
        }
    }

    private static async Task HandleGraphPatternsApi(HttpListenerContext context)
    {
        var response = context.Response;

        try
        {
            var patterns = FalkorDBVectorStore.GetCommonPatterns();

            response.ContentType = "application/json";
            response.StatusCode = 200;
            var resultJson = JsonSerializer.Serialize(patterns, JsonOptions);
            var resultBuffer = Encoding.UTF8.GetBytes(resultJson);
            response.ContentLength64 = resultBuffer.Length;
            response.OutputStream.Write(resultBuffer, 0, resultBuffer.Length);
            response.Close();
            HttpServerService.LogRequest(context.Request, 200);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ Graph Patterns API Error: {ex.Message}");
            response.StatusCode = 500;
            var errorJson = $"{{\"error\":\"{WebUtility.HtmlEncode(ex.Message)}\"}}";
            var buffer = Encoding.UTF8.GetBytes(errorJson);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
            HttpServerService.LogRequest(context.Request, 500);
        }
    }

    // ============================================================
    // MCP Tool Handlers for Graph Queries
    // ============================================================

    private static async Task<CallToolResult> HandleQueryGraphTool(Dictionary<string, object>? arguments)
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
            var store = VectorStoreFactory.Create(_storeType ?? "json", _connectionString, _indexName) as FalkorDBVectorStore;
            if (store == null)
            {
                return McpProtocolService.CreateToolError("FalkorDB store is required for graph queries");
            }

            Dictionary<string, object>? parameters = null;
            if (arguments.TryGetValue("parameters", out var paramsObj) && paramsObj != null)
            {
                parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(paramsObj));
            }

            var result = await store.ExecuteCypherQuery(query, parameters);
            
            if (result.Success)
            {
                var resultText = $"Query returned {result.RowCount} rows in {result.ExecutionTimeMs:F2}ms:\n\n";
                resultText += JsonSerializer.Serialize(result.Results, new JsonSerializerOptions { WriteIndented = true });
                return McpProtocolService.CreateToolResult(resultText);
            }
            else
            {
                return McpProtocolService.CreateToolError($"Query failed: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            return McpProtocolService.CreateToolError($"Error executing graph query: {ex.Message}");
        }
    }

    private static async Task<CallToolResult> HandleGraphStatisticsTool()
    {
        try
        {
            var store = VectorStoreFactory.Create(_storeType ?? "json", _connectionString, _indexName) as FalkorDBVectorStore;
            if (store == null)
            {
                return McpProtocolService.CreateToolError("FalkorDB store is required");
            }

            var stats = await store.GetGraphStatistics();
            var text = $"Graph Statistics:\n\n" +
                      $"Total Nodes: {stats.NodeCount}\n" +
                      $"Total Relationships: {stats.RelationshipCount}\n\n" +
                      $"Nodes by Label:\n" +
                      string.Join("\n", stats.NodesByLabel.Select(kvp => $"  {kvp.Key}: {kvp.Value}")) +
                      $"\n\nRelationships by Type:\n" +
                      string.Join("\n", stats.RelationshipsByType.Select(kvp => $"  {kvp.Key}: {kvp.Value}"));

            return McpProtocolService.CreateToolResult(text);
        }
        catch (Exception ex)
        {
            return McpProtocolService.CreateToolError($"Error getting graph statistics: {ex.Message}");
        }
    }

    private static async Task<CallToolResult> HandleServiceDependenciesTool(Dictionary<string, object>? arguments)
    {
        if (arguments == null || !arguments.TryGetValue("serviceId", out var serviceIdObj))
        {
            return McpProtocolService.CreateToolError("Missing 'serviceId' parameter");
        }

        var serviceId = serviceIdObj?.ToString();
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return McpProtocolService.CreateToolError("Service ID cannot be empty");
        }

        try
        {
            var store = VectorStoreFactory.Create(_storeType ?? "json", _connectionString, _indexName) as FalkorDBVectorStore;
            if (store == null)
            {
                return McpProtocolService.CreateToolError("FalkorDB store is required");
            }

            var dependencies = await store.GetServiceDependencies(serviceId);
            var dependents = await store.GetServiceDependents(serviceId);
            
            var text = $"Dependency Analysis for '{serviceId}':\n\n";
            text += $"Dependencies ({dependencies.Count}): What '{serviceId}' depends on\n";
            foreach (var dep in dependencies)
            {
                text += $"  - {dep.Id} ({dep.Label})\n";
            }
            
            text += $"\nDependents ({dependents.Count}): What depends on '{serviceId}'\n";
            foreach (var dep in dependents)
            {
                text += $"  - {dep.Id} ({dep.Label})\n";
            }

            var includeImpact = arguments.TryGetValue("includeImpact", out var impactObj) && 
                               impactObj is bool b && b;
            
            if (includeImpact)
            {
                var impact = await store.GetImpactAnalysis(serviceId);
                text += $"\n\nImpact Analysis ({impact.Count}): All services affected if '{serviceId}' fails\n";
                foreach (var dep in impact)
                {
                    text += $"  - {dep.Id} ({dep.Label})\n";
                }
            }

            return McpProtocolService.CreateToolResult(text);
        }
        catch (Exception ex)
        {
            return McpProtocolService.CreateToolError($"Error analyzing dependencies: {ex.Message}");
        }
    }

    private static async Task<CallToolResult> HandleTeamOwnershipTool(Dictionary<string, object>? arguments)
    {
        if (arguments == null || !arguments.TryGetValue("teamId", out var teamIdObj))
        {
            return McpProtocolService.CreateToolError("Missing 'teamId' parameter");
        }

        var teamId = teamIdObj?.ToString();
        if (string.IsNullOrWhiteSpace(teamId))
        {
            return McpProtocolService.CreateToolError("Team ID cannot be empty");
        }

        try
        {
            var store = VectorStoreFactory.Create(_storeType ?? "json", _connectionString, _indexName) as FalkorDBVectorStore;
            if (store == null)
            {
                return McpProtocolService.CreateToolError("FalkorDB store is required");
            }

            var ownership = await store.GetTeamOwnership(teamId);
            
            var text = $"Team Ownership for '{teamId}':\n\n";
            
            if (ownership.TryGetValue("services", out var services))
            {
                text += $"Services ({services.Count}):\n";
                text += string.Join("\n", services.Select(s => $"  - {s.Id}"));
            }
            
            if (ownership.TryGetValue("resources", out var resources))
            {
                text += $"\n\nResources ({resources.Count}):\n";
                text += string.Join("\n", resources.Select(r => $"  - {r.Id}"));
            }

            return McpProtocolService.CreateToolResult(text);
        }
        catch (Exception ex)
        {
            return McpProtocolService.CreateToolError($"Error getting team ownership: {ex.Message}");
        }
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
