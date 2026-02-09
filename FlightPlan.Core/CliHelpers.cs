namespace FlightPlan;

public static class CliHelpers
{
    public static void EnsureFileExists(FileInfo file, string purpose)
    {
        if (file is null)
        {
            Console.Error.WriteLine($"error: Missing {purpose}.");
            Environment.Exit(1);
        }

        if (!file.Exists)
        {
            Console.Error.WriteLine($"error: {purpose} not found: {file.FullName}");
            Environment.Exit(1);
        }
    }

    public static void EnsureOutputFileWritable(FileInfo output, bool overwrite)
    {
        if (output is null)
        {
            Console.Error.WriteLine("error: Missing output file path.");
            Environment.Exit(1);
        }

        // Prevent common foot-gun: passing a directory path where a file is expected.
        if (Directory.Exists(output.FullName))
        {
            Console.Error.WriteLine($"error: Output path is a directory: {output.FullName}");
            Environment.Exit(1);
        }

        if (File.Exists(output.FullName) && !overwrite)
        {
            Console.Error.WriteLine($"error: Output file already exists: {output.FullName}. Pass --overwrite to replace it.");
            Environment.Exit(1);
        }

        var outputDir = Path.GetDirectoryName(output.FullName);
        if (!string.IsNullOrWhiteSpace(outputDir))
            Directory.CreateDirectory(outputDir);
    }

    public static void WriteOutputTextFile(FileInfo output, string contents, bool overwrite)
    {
        EnsureOutputFileWritable(output, overwrite);
        File.WriteAllText(output.FullName, contents);
    }
}
