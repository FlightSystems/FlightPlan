using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlightPlan.Commands;

internal static class IndexCommand
{
    internal static void Configure(
        Command indexCmd,
        Argument<FileInfo> compiledJsonArg,
        Option<string> ollamaUrlOpt,
        Option<string> embeddingModelOpt,
        Option<bool> rebuildOpt,
        Option<string?> includeDocsOpt,
        Option<string?> baseUrlOpt,
        Option<string> storeTypeOpt,
        Option<string?> connectionStringOpt,
        Option<string?> indexNameOpt)
    {
        indexCmd.SetHandler(async (context) =>
        {
            var compiledJson = context.ParseResult.GetValueForArgument(compiledJsonArg);
            var ollamaUrl = context.ParseResult.GetValueForOption(ollamaUrlOpt) ?? "http://localhost:11434";
            var embeddingModel = context.ParseResult.GetValueForOption(embeddingModelOpt) ?? "nomic-embed-text";
            var rebuild = context.ParseResult.GetValueForOption(rebuildOpt);
            var includeDocs = context.ParseResult.GetValueForOption(includeDocsOpt);
            var baseUrl = context.ParseResult.GetValueForOption(baseUrlOpt);
            var storeType = context.ParseResult.GetValueForOption(storeTypeOpt) ?? "json";
            var connectionString = context.ParseResult.GetValueForOption(connectionStringOpt);
            var indexName = context.ParseResult.GetValueForOption(indexNameOpt);
            
            CliHelpers.EnsureFileExists(compiledJson, "Compiled FlightPlan JSON file");

            // Validate store type
            if (!VectorStoreFactory.IsSupported(storeType))
            {
                Console.Error.WriteLine($"❌ Unknown store type: {storeType}");
                Console.Error.WriteLine($"   Supported types: {string.Join(", ", VectorStoreFactory.SupportedTypes)}");
                Environment.Exit(1);
            }

            // Derive graph name from FlightPlan or use provided name
            string? graphName = indexName;
            if (storeType == "falkordb" && string.IsNullOrEmpty(graphName))
            {
                graphName = await DeriveGraphName(compiledJson);
                Console.WriteLine($"   Derived index name: {graphName}");
            }

            var store = VectorStoreFactory.Create(storeType, connectionString, graphName);
            
            // Check if index exists
            if (store.IndexExists() && !rebuild)
            {
                Console.WriteLine("⚠️  Index already exists. Use --rebuild to recreate it.");
                Environment.Exit(0);
            }

            if (rebuild && store.IndexExists())
            {
                Console.WriteLine("🗑️  Deleting existing index...");
                store.DeleteIndex();
            }

            Console.WriteLine($"🚀 Building vector index for FlightPlan queries using {storeType.ToUpper()}...");
            Console.WriteLine($"   Store type: {storeType}");
            Console.WriteLine($"   Using model: {embeddingModel}");
            Console.WriteLine($"   Ollama URL: {ollamaUrl}");
            if (storeType == "falkordb")
            {
                Console.WriteLine($"   FalkorDB: {connectionString ?? "localhost:6379"}");
                Console.WriteLine($"   Index name: {graphName}");
            }
            if (!string.IsNullOrEmpty(includeDocs))
            {
                Console.WriteLine($"   Including docs: {includeDocs}");
            }
            if (!string.IsNullOrEmpty(baseUrl))
            {
                Console.WriteLine($"   Base URL: {baseUrl}");
            }
            Console.WriteLine();

            try
            {
                var chunkCount = await store.IndexFlightPlan(compiledJson, includeDocs, baseUrl, ollamaUrl, embeddingModel);
                
                Console.WriteLine($"\n✔ Index built successfully!");
                Console.WriteLine($"   Store: {storeType}");
                Console.WriteLine($"   Indexed {chunkCount} chunks");
                if (storeType == "json")
                {
                    Console.WriteLine($"   Saved to: .flightplan.index.json");
                }
                else if (storeType == "falkordb")
                {
                    Console.WriteLine($"   Saved to: FalkorDB at {connectionString ?? "localhost:6379"}");
                }
                Console.WriteLine($"\nYou can now query with: flight query \"your question\"");
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"❌ Failed to connect to Ollama at {ollamaUrl}");
                Console.Error.WriteLine($"   Error: {ex.Message}");
                Console.Error.WriteLine($"\n   Make sure Ollama is running: ollama serve");
                Console.Error.WriteLine($"   And the embedding model is available: ollama pull {embeddingModel}");
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"❌ Indexing failed: {ex.Message}");
                if (storeType == "falkordb" && ex.Message.Contains("connection"))
                {
                    Console.Error.WriteLine($"\n   Make sure FalkorDB/Redis is running at {connectionString ?? "localhost:6379"}");
                    Console.Error.WriteLine($"   You can start it with: docker run -p 6379:6379 falkordb/falkordb");
                }
                Environment.Exit(1);
            }

        });
    }

    /// <summary>
    /// Derive a safe graph name from the FlightPlan application name
    /// </summary>
    private static async Task<string> DeriveGraphName(FileInfo compiledJson)
    {
        try
        {
            var jsonContent = await File.ReadAllTextAsync(compiledJson.FullName);
            using var doc = JsonDocument.Parse(jsonContent);
            
            // Try to get application.name
            if (doc.RootElement.TryGetProperty("application", out var app) &&
                app.TryGetProperty("name", out var name))
            {
                var appName = name.GetString();
                if (!string.IsNullOrEmpty(appName))
                {
                    // Convert to lowercase and replace non-alphanumeric chars with hyphens
                    return SanitizeGraphName(appName);
                }
            }
            
            // Fallback to filename without extension
            var fileName = Path.GetFileNameWithoutExtension(compiledJson.Name);
            return SanitizeGraphName(fileName);
        }
        catch
        {
            // If we can't parse, use filename
            return SanitizeGraphName(Path.GetFileNameWithoutExtension(compiledJson.Name));
        }
    }

    /// <summary>
    /// Convert a name to a safe graph name (lowercase, alphanumeric + hyphens)
    /// </summary>
    private static string SanitizeGraphName(string name)
    {
        // Convert to lowercase
        name = name.ToLowerInvariant();
        
        // Replace spaces and underscores with hyphens
        name = name.Replace(' ', '-').Replace('_', '-');
        
        // Remove any non-alphanumeric characters except hyphens
        name = Regex.Replace(name, @"[^a-z0-9\-]", "");
        
        // Replace multiple consecutive hyphens with single hyphen
        name = Regex.Replace(name, @"\-+", "-");
        
        // Remove leading/trailing hyphens
        name = name.Trim('-');
        
        // Ensure it's not empty
        if (string.IsNullOrEmpty(name))
        {
            name = "flightplan";
        }
        
        return name;
    }
}
