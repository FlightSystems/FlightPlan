using System.CommandLine;

namespace FlightPlan.Commands;

internal static class ValidatePlanCommand
{
    internal static void Configure(Command validatePlanCmd, Argument<FileInfo> inputArg)
    {
        validatePlanCmd.SetHandler((FileInfo input) =>
        {
            CliHelpers.EnsureFileExists(input, "Flight Plan YAML file");

            var compiler = new FlightPlanCompiler();
            var yamlText = File.ReadAllText(input.FullName);
            var result = compiler.Compile(yamlText, Path.GetDirectoryName(input.FullName)!);
            CompilerCliHelpers.PrintDiagnostics(result);

            if (result.Errors.Any())
                Environment.Exit(1);

            Console.WriteLine("✔ Flight Plan validated successfully.");
        }, inputArg);
    }
}
