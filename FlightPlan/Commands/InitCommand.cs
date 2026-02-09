using System.CommandLine;
using System.Text;

namespace FlightPlan.Commands;

internal static class InitCommand
{
    private const string EmbeddedBaseDataclassesResourceName = "FlightPlan.Cli.Embedded.base-dataclasses.yaml";
    private const string EmbeddedBasePlatformsResourceName = "FlightPlan.Cli.Embedded.base-platforms.yaml";
    private const string EmbeddedOrgTeamsResourceName = "FlightPlan.Cli.Embedded.org-teams.yaml";
    private const string EmbeddedOrgZonesResourceName = "FlightPlan.Cli.Embedded.org-zones.yaml";
    private const string EmbeddedOrgEnvironmentsResourceName = "FlightPlan.Cli.Embedded.org-environments.yaml";
    private const string EmbeddedFlightPlanResourceName = "FlightPlan.Cli.Embedded.flightplan.yaml";

    internal static void Configure(Command initCmd)
    {
        var directoryOpt = new Option<DirectoryInfo?>(
            aliases: ["-o", "--out"],
            description: "Output directory to create the scaffolded files in (defaults to current directory)")
        {
            IsRequired = false
        };

        var overwriteOpt = new Option<bool>(
            name: "--overwrite",
            description: "Overwrite existing files if they already exist")
        {
            IsRequired = false
        };

        initCmd.AddOption(directoryOpt);
        initCmd.AddOption(overwriteOpt);

        initCmd.SetHandler((DirectoryInfo? dir, bool overwrite) =>
        {
            try
            {
                var targetDir = dir?.FullName;
                if (string.IsNullOrWhiteSpace(targetDir))
                    targetDir = Directory.GetCurrentDirectory();

                targetDir = Path.GetFullPath(targetDir);

                var flightplanPath = Path.Combine(targetDir, "flightplan.yaml");
                var baseDataClassesPath = Path.Combine(targetDir, "base-dataclasses.yaml");
                var basePlatformsPath = Path.Combine(targetDir, "base-platforms.yaml");
                var orgTeamsPath = Path.Combine(targetDir, "org-teams.yaml");
                var orgZonesPath = Path.Combine(targetDir, "org-zones.yaml");
                var orgEnvironmentsPath = Path.Combine(targetDir, "org-environments.yaml");

                var files = new[]
                {
                    new FileInfo(flightplanPath),
                    new FileInfo(baseDataClassesPath),
                    new FileInfo(basePlatformsPath),
                    new FileInfo(orgTeamsPath),
                    new FileInfo(orgZonesPath),
                    new FileInfo(orgEnvironmentsPath)
                };

                // Preflight checks so we don't partially write.
                if (!overwrite)
                {
                    var existing = files.Where(f => f.Exists || Directory.Exists(f.FullName)).Select(f => f.FullName).ToList();
                    if (existing.Count > 0)
                    {
                        Console.Error.WriteLine("error: Refusing to overwrite existing file(s). Pass --overwrite to replace them:");
                        foreach (var p in existing)
                            Console.Error.WriteLine($"  - {p}");
                        Environment.Exit(1);
                    }
                }

                Directory.CreateDirectory(targetDir);

                CliHelpers.WriteOutputTextFile(new FileInfo(baseDataClassesPath), ReadEmbeddedText(EmbeddedBaseDataclassesResourceName), overwrite);
                CliHelpers.WriteOutputTextFile(new FileInfo(basePlatformsPath), ReadEmbeddedText(EmbeddedBasePlatformsResourceName), overwrite);
                CliHelpers.WriteOutputTextFile(new FileInfo(orgTeamsPath), ReadEmbeddedText(EmbeddedOrgTeamsResourceName), overwrite);
                CliHelpers.WriteOutputTextFile(new FileInfo(orgZonesPath), ReadEmbeddedText(EmbeddedOrgZonesResourceName), overwrite);
                CliHelpers.WriteOutputTextFile(new FileInfo(orgEnvironmentsPath), ReadEmbeddedText(EmbeddedOrgEnvironmentsResourceName), overwrite);
                CliHelpers.WriteOutputTextFile(new FileInfo(flightplanPath), ReadEmbeddedText(EmbeddedFlightPlanResourceName), overwrite);

                Console.WriteLine($"✔ Initialized Flight Plan scaffold in {targetDir}");
                Console.WriteLine($"✔ Created: {Path.GetFileName(flightplanPath)}");
                Console.WriteLine("✔ Next steps:");
                Console.WriteLine($"  - flightplan verify {Path.GetFileName(flightplanPath)}");
                Console.WriteLine($"  - flightplan report {Path.GetFileName(flightplanPath)}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: Failed to initialize scaffold: {ex.Message}");
                Environment.Exit(1);
            }
        }, directoryOpt, overwriteOpt);
    }

      private static string ReadEmbeddedText(string resourceName)
      {
        var assembly = typeof(InitCommand).Assembly;
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
