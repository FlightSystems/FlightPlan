using System.CommandLine;

namespace FlightPlan.Commands;

internal static class VerifyCommand
{
    internal static void Configure(Command verifyCmd, Argument<FileInfo> inputArg)
    {
        verifyCmd.SetHandler((FileInfo input) =>
        {
            CliHelpers.EnsureFileExists(input, "Flight Plan YAML file");

            var compiler = new FlightPlanCompiler();
            var yamlText = File.ReadAllText(input.FullName);
            var result = compiler.Compile(yamlText, Path.GetDirectoryName(input.FullName)!);
            CompilerCliHelpers.PrintDiagnostics(result);

            if (result.Errors.Any())
                Environment.Exit(1);

            Console.WriteLine("✔ Flight Plan verified successfully.");
        }, inputArg);
    }
}
