using System.CommandLine;

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
        Option<string?> baseUrlOpt)
    {
        indexCmd.SetHandler(async (compiledJson, ollamaUrl, embeddingModel, rebuild, includeDocs, baseUrl) =>
        {
            CliHelpers.EnsureFileExists(compiledJson, "Compiled FlightPlan JSON file");

            var store = new VectorStore();
            
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

            Console.WriteLine("🚀 Building vector index for FlightPlan queries...");
            Console.WriteLine($"   Using model: {embeddingModel}");
            Console.WriteLine($"   Ollama URL: {ollamaUrl}");
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
                Console.WriteLine($"   Indexed {chunkCount} chunks");
                Console.WriteLine($"   Saved to: .flightplan.index.json");
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
                Environment.Exit(1);
            }

        }, compiledJsonArg, ollamaUrlOpt, embeddingModelOpt, rebuildOpt, includeDocsOpt, baseUrlOpt);
    }
}
