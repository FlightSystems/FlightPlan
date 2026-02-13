using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using FlightPlan.Commands;

namespace FlightPlan;

public static class Program
{
    public static int Main(string[] args)
    {
        var root = new RootCommand("Flight Plan CLI");

        // ------------------------------------------------------------
        // Shared arguments / options
        // ------------------------------------------------------------

        var inputArg = new Argument<FileInfo>(
            name: "input",
            description: "Root Flight Plan YAML file");

        var buildOutputOpt = new Option<FileInfo?>(
            aliases: ["-o", "--out"],
            description: "Output compiled JSON file");

        var reportOutputOpt = new Option<FileInfo?>(
            aliases: ["-o", "--out"],
            description: "Output report file (defaults to stdout if omitted)");

        var analyzeOutputOpt = new Option<FileInfo?>(
            aliases: ["-o", "--out"],
            description: "Output reconciliation report file (defaults to stdout if omitted)");

        var overwriteOpt = new Option<bool>(
            name: "--overwrite",
            description: "Overwrite the output file if it already exists")
        {
            IsRequired = false
        };

        // var includeUnreferencedOpt = new Option<List<string>>(
        //     name: "--include-unreferenced",
        //     description: "Entity kinds to include even if unreferenced")
        // {
        //     AllowMultipleArgumentsPerToken = true
        // };

        // ------------------------------------------------------------
        // VERIFY
        // ------------------------------------------------------------

        var verifyCmd = new Command("verify", "Validate Flight Plan only");
        verifyCmd.AddArgument(inputArg);
        VerifyCommand.Configure(verifyCmd, inputArg);

        // ------------------------------------------------------------
        // INIT
        // ------------------------------------------------------------

        var initCmd = new Command("init", "Scaffold a new flightplan.yaml (and supporting registry files)");
        InitCommand.Configure(initCmd);

        // ------------------------------------------------------------
        // VALIDATE
        // ------------------------------------------------------------

        var validateCmd = new Command("validate", "Validate inputs");

        var validatePlanCmd = new Command("plan", "Validate a Flight Plan YAML file (schema + referential integrity)");
        validatePlanCmd.AddArgument(inputArg);
        ValidatePlanCommand.Configure(validatePlanCmd, inputArg);

        validateCmd.AddCommand(validatePlanCmd);

        // ------------------------------------------------------------
        // REPORT
        // ------------------------------------------------------------

        var reportCmd = new Command("report", "Generate system overview report");
        reportCmd.AddArgument(inputArg);
        var formatOpt = new Option<string>("--format", () => "text", "Output format: text or html");
        reportCmd.AddOption(formatOpt);
        var typeOpt = new Option<string>("--type", () => "architecture", "Report type: architecture, security, onboarding, or service-catalog");
        reportCmd.AddOption(typeOpt);
        reportCmd.AddOption(reportOutputOpt);
        reportCmd.AddOption(overwriteOpt);
        
        var aiEnhanceOpt = new Option<bool>(
            name: "--ai-enhance",
            description: "Enhance report with AI-generated narratives and insights using Ollama")
        {
            IsRequired = false
        };
        reportCmd.AddOption(aiEnhanceOpt);
        
        var aiModelOpt = new Option<string>(
            name: "--ai-model",
            () => "llama3.2",
            description: "Ollama model to use for AI enhancement");
        reportCmd.AddOption(aiModelOpt);
        
        var aiLogOpt = new Option<FileInfo?>(
            name: "--ai-log",
            description: "Save AI prompts and responses to JSON file (default: <output>.ai-log.json)")
        {
            IsRequired = false
        };
        reportCmd.AddOption(aiLogOpt);
        
        var showEmptyOpt = new Option<bool>(
            name: "--show-empty",
            description: "Show empty sections and rows with 'Not modeled yet' placeholder instead of hiding them")
        {
            IsRequired = false
        };
        reportCmd.AddOption(showEmptyOpt);
        
        ReportCommand.Configure(reportCmd, inputArg, formatOpt, typeOpt, reportOutputOpt, overwriteOpt, aiEnhanceOpt, aiModelOpt, aiLogOpt, showEmptyOpt);

        // ------------------------------------------------------------
        // BUILD
        // ------------------------------------------------------------

        var buildCmd = new Command("build", "Compile Flight Plan into JSON");
        buildCmd.AddArgument(inputArg);
        buildCmd.AddOption(buildOutputOpt);
        buildCmd.AddOption(overwriteOpt);
        //buildCmd.AddOption(includeUnreferencedOpt);
        BuildCommand.Configure(buildCmd, inputArg, buildOutputOpt, overwriteOpt);

        // ------------------------------------------------------------
        // PUBLISH
        // ------------------------------------------------------------

        var publishCmd = new Command("publish", "Validate, build, and generate all reports");
        publishCmd.AddArgument(inputArg);
        publishCmd.AddOption(formatOpt);

        var publishOutDirOpt = new Option<DirectoryInfo?>(
            aliases: ["-o", "--output"],
            description: "Output directory for published artifacts")
        {
            IsRequired = false
        };
        publishCmd.AddOption(publishOutDirOpt);
        var publishForceOpt = new Option<bool>(
            aliases: ["--force", "--overwrite"],
            description: "Overwrite the output directory if it already exists")
        {
            IsRequired = false
        };
        publishCmd.AddOption(publishForceOpt);

        var publishZipOpt = new Option<FileInfo?>(
            name: "--zip",
            description: "Write the published outputs to a .zip file (alternative to -o/--output)")
        {
            IsRequired = false
        };
        publishCmd.AddOption(publishZipOpt);

        var publishOpenOpt = new Option<bool>(
            name: "--open",
            description: "Open the generated HTML table-of-contents report in your default browser (requires --format html)")
        {
            IsRequired = false
        };
        publishCmd.AddOption(publishOpenOpt);

        var publishAiEnhanceOpt = new Option<bool>(
            name: "--ai-enhance",
            description: "Enhance reports with AI-generated narratives and insights using Ollama")
        {
            IsRequired = false
        };
        publishCmd.AddOption(publishAiEnhanceOpt);

        var publishAiModelOpt = new Option<string>(
            name: "--ai-model",
            () => "llama3.2",
            description: "Ollama model to use for AI enhancement");
        publishCmd.AddOption(publishAiModelOpt);

        PublishCommand.Configure(publishCmd, inputArg, formatOpt, publishOutDirOpt, publishForceOpt, publishZipOpt, publishOpenOpt, publishAiEnhanceOpt, publishAiModelOpt);

        // ------------------------------------------------------------
        // SERVE
        // ------------------------------------------------------------

        var serveCmd = new Command("serve", "Start a local web server to browse documentation");
        
        var serveDirArg = new Argument<DirectoryInfo>(
            name: "directory",
            description: "Directory containing the documentation files (markdown or HTML)");
        
        var servePortOpt = new Option<int>(
            name: "--port",
            () => 8080,
            description: "Port number for the web server");
        
        var serveOpenOpt = new Option<bool>(
            name: "--open",
            description: "Automatically open the browser")
        {
            IsRequired = false
        };
        
        var serveStoreTypeOpt = new Option<string>(
            name: "--store-type",
            description: "Vector store type: json (default) or falkordb")
        {
            IsRequired = false
        };
        serveStoreTypeOpt.SetDefaultValue("json");

        var serveConnectionStringOpt = new Option<string?>(
            name: "--connection-string",
            description: "Connection string (for falkordb: host:port, for json: file path)")
        {
            IsRequired = false
        };

        var serveIndexNameOpt = new Option<string?>(
            name: "--index-name",
            description: "Index/graph name to query (for falkordb, auto-detects if not specified)")
        {
            IsRequired = false
        };

        serveCmd.AddArgument(serveDirArg);
        serveCmd.AddOption(servePortOpt);
        serveCmd.AddOption(serveOpenOpt);
        serveCmd.AddOption(serveStoreTypeOpt);
        serveCmd.AddOption(serveConnectionStringOpt);
        serveCmd.AddOption(serveIndexNameOpt);
        ServeCommand.Configure(serveCmd, serveDirArg, servePortOpt, serveOpenOpt, serveStoreTypeOpt, serveConnectionStringOpt, serveIndexNameOpt);

        // ------------------------------------------------------------
        // INDEX (AI/RAG)
        // ------------------------------------------------------------

        var indexCmd = new Command("index", "Build vector index for AI-powered queries");

        var indexInputArg = new Argument<FileInfo>(
            name: "compiled-json",
            description: "Compiled FlightPlan JSON file");

        var indexOllamaUrlOpt = new Option<string>(
            name: "--ollama-url",
            description: "Ollama API base URL")
        {
            IsRequired = false
        };
        indexOllamaUrlOpt.SetDefaultValue("http://localhost:11434");

        var embeddingModelOpt = new Option<string>(
            name: "--embedding-model",
            description: "Ollama embedding model to use")
        {
            IsRequired = false
        };
        embeddingModelOpt.SetDefaultValue("nomic-embed-text");

        var rebuildOpt = new Option<bool>(
            name: "--rebuild",
            description: "Rebuild the index even if it already exists")
        {
            IsRequired = false
        };

        var includeDocsOpt = new Option<string?>(
            name: "--include-docs",
            description: "Directory containing markdown documentation to include in index")
        {
            IsRequired = false
        };

        var baseUrlOpt = new Option<string?>(
            name: "--base-url",
            description: "Base URL for documentation links (e.g., https://github.com/org/repo/blob/main/)")
        {
            IsRequired = false
        };

        var storeTypeOpt = new Option<string>(
            name: "--store-type",
            description: "Vector store type: json (default) or falkordb")
        {
            IsRequired = false
        };
        storeTypeOpt.SetDefaultValue("json");

        var connectionStringOpt = new Option<string?>(
            name: "--connection-string",
            description: "Connection string (for falkordb: host:port, for json: file path)")
        {
            IsRequired = false
        };

        var indexNameOpt = new Option<string?>(
            name: "--index-name",
            description: "Index/graph name (for falkordb, auto-derived from application name if not specified)")
        {
            IsRequired = false
        };

        indexCmd.AddArgument(indexInputArg);
        indexCmd.AddOption(indexOllamaUrlOpt);
        indexCmd.AddOption(embeddingModelOpt);
        indexCmd.AddOption(rebuildOpt);
        indexCmd.AddOption(includeDocsOpt);
        indexCmd.AddOption(baseUrlOpt);
        indexCmd.AddOption(storeTypeOpt);
        indexCmd.AddOption(connectionStringOpt);
        indexCmd.AddOption(indexNameOpt);
        IndexCommand.Configure(indexCmd, indexInputArg, indexOllamaUrlOpt, embeddingModelOpt, rebuildOpt, includeDocsOpt, baseUrlOpt, storeTypeOpt, connectionStringOpt, indexNameOpt);

        // ------------------------------------------------------------
        // QUERY (AI/Ollama)
        // ------------------------------------------------------------

        var queryCmd = new Command("query", "Query the FlightPlan using AI (Ollama with RAG)");

        var queryArg = new Argument<string>(
            name: "question",
            description: "Question to ask about the FlightPlan");

        var modelOpt = new Option<string>(
            name: "--model",
            description: "Ollama model to use (e.g., llama3.2, mistral, codellama)")
        {
            IsRequired = false
        };
        modelOpt.SetDefaultValue("llama3.2");

        var ollamaUrlOpt = new Option<string>(
            name: "--ollama-url",
            description: "Ollama API base URL")
        {
            IsRequired = false
        };
        ollamaUrlOpt.SetDefaultValue("http://localhost:11434");

        var streamOpt = new Option<bool>(
            name: "--stream",
            description: "Stream the response token-by-token (default: true)")
        {
            IsRequired = false
        };
        streamOpt.SetDefaultValue(true);

        var maxTokensOpt = new Option<int?>(
            name: "--max-tokens",
            description: "Maximum tokens to generate in response")
        {
            IsRequired = false
        };

        var queryEmbeddingModelOpt = new Option<string>(
            name: "--embedding-model",
            description: "Ollama embedding model to use (must match index)")
        {
            IsRequired = false
        };
        queryEmbeddingModelOpt.SetDefaultValue("nomic-embed-text");

        var topKOpt = new Option<int>(
            name: "--top-k",
            description: "Number of relevant chunks to retrieve")
        {
            IsRequired = false
        };
        topKOpt.SetDefaultValue(5);

        var queryStoreTypeOpt = new Option<string>(
            name: "--store-type",
            description: "Vector store type: json (default) or falkordb")
        {
            IsRequired = false
        };
        queryStoreTypeOpt.SetDefaultValue("json");

        var queryConnectionStringOpt = new Option<string?>(
            name: "--connection-string",
            description: "Connection string (for falkordb: host:port, for json: file path)")
        {
            IsRequired = false
        };

        var queryIndexNameOpt = new Option<string?>(
            name: "--index-name",
            description: "Index/graph name to query (for falkordb, auto-detects if not specified)")
        {
            IsRequired = false
        };

        queryCmd.AddArgument(queryArg);
        queryCmd.AddOption(modelOpt);
        queryCmd.AddOption(ollamaUrlOpt);
        queryCmd.AddOption(streamOpt);
        queryCmd.AddOption(maxTokensOpt);
        queryCmd.AddOption(queryEmbeddingModelOpt);
        queryCmd.AddOption(topKOpt);
        queryCmd.AddOption(queryStoreTypeOpt);
        queryCmd.AddOption(queryConnectionStringOpt);
        queryCmd.AddOption(queryIndexNameOpt);
        QueryCommand.Configure(queryCmd, queryArg, modelOpt, ollamaUrlOpt, streamOpt, maxTokensOpt, queryEmbeddingModelOpt, topKOpt, queryStoreTypeOpt, queryConnectionStringOpt, queryIndexNameOpt);

        // ------------------------------------------------------------
        // Wire up
        // ------------------------------------------------------------

        // ------------------------------------------------------------
        // GENERATE
        // ------------------------------------------------------------

        var generateCmd = new Command("generate", "Generate various artifacts");
        generateCmd.AddCommand(GenerateAiAugmentationsCommand.Create());

        root.AddCommand(verifyCmd);
        root.AddCommand(initCmd);
        root.AddCommand(validateCmd);
        root.AddCommand(buildCmd);
        root.AddCommand(reportCmd);
        root.AddCommand(publishCmd);
        root.AddCommand(serveCmd);
        root.AddCommand(generateCmd);
        root.AddCommand(indexCmd);
        root.AddCommand(queryCmd);

        var parser = new CommandLineBuilder(root)
            .UseDefaults()
            .UseVersionOption()
            .Build();

        return parser.Invoke(args);
    }
}
