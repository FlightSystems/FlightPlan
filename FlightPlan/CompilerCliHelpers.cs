using System.Text.Json;

namespace FlightPlan;

internal static class CompilerCliHelpers
{
    internal static void PrintDiagnostics(CompilationResult result)
    {
        foreach (var error in result.Errors)
        {
            Console.Error.WriteLine($"error: {error}");
        }
    }

    internal static CompiledFlightPlan LoadPlan(FileInfo input)
    {
        CliHelpers.EnsureFileExists(input, "Flight Plan input file");

        var ext = Path.GetExtension(input.FullName).ToLowerInvariant();
        if (ext == ".json")
        {
            var jsonText = File.ReadAllText(input.FullName);
            var plan = JsonSerializer.Deserialize<CompiledFlightPlan>(jsonText, CompiledFlightPlanJson.CreateOptions());
            if (plan is null)
            {
                Console.Error.WriteLine($"error: Failed to parse compiled plan JSON: {input.FullName}");
                Environment.Exit(1);
            }

            return plan;
        }

        // Default: treat as YAML flight plan
        var compiler = new FlightPlanCompiler();
        var yamlText = File.ReadAllText(input.FullName);
        var result = compiler.Compile(yamlText, Path.GetDirectoryName(input.FullName)!);
        PrintDiagnostics(result);

        if (result.Errors.Any())
            Environment.Exit(1);

        return result.Plan!;
    }

}