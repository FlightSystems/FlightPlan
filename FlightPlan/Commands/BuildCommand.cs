using System.CommandLine;
using System.Text.Json;

namespace FlightPlan.Commands;

internal static class BuildCommand
{
    internal static void Configure(Command buildCmd, Argument<FileInfo> inputArg, Option<FileInfo?> outputOpt, Option<bool> overwriteOpt)
    {
        buildCmd.SetHandler((FileInfo input, FileInfo? output, bool overwrite) =>
        {
            CliHelpers.EnsureFileExists(input, "Flight Plan YAML file");

            var compiler = new FlightPlanCompiler();
            var yamlText = File.ReadAllText(input.FullName);
            var result = compiler.Compile(yamlText, Path.GetDirectoryName(input.FullName)!);

            CompilerCliHelpers.PrintDiagnostics(result);

            if (result.Errors.Any())
                Environment.Exit(1);

            var json = JsonSerializer.Serialize(result.Plan, CompiledFlightPlanJson.CreateOptions());

            if (output != null)
                CliHelpers.WriteOutputTextFile(output, json, overwrite);
            else
                Console.WriteLine(json);

            Console.WriteLine("✔ Build completed successfully.");
        }, inputArg, outputOpt, overwriteOpt);
    }
}
