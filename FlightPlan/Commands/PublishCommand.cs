using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using FlightPlan.Reporting;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FlightPlan.Commands;

internal static class PublishCommand
{
    internal static void Configure(
        Command publishCmd,
        Argument<FileInfo> inputArg,
        Option<string> formatOpt,
        Option<DirectoryInfo?> outDirOpt,
        Option<bool> forceOpt,
        Option<FileInfo?> zipOpt,
        Option<bool> openOpt,
        Option<bool> aiEnhanceOpt,
        Option<string> aiModelOpt)
    {
        publishCmd.SetHandler(async (InvocationContext ctx) =>
        {
            try
            {
                var input = ctx.ParseResult.GetValueForArgument(inputArg);
                var format = ctx.ParseResult.GetValueForOption(formatOpt) ?? "markdown";
                var outDir = ctx.ParseResult.GetValueForOption(outDirOpt);
                var force = ctx.ParseResult.GetValueForOption(forceOpt);
                var zip = ctx.ParseResult.GetValueForOption(zipOpt);
                var open = ctx.ParseResult.GetValueForOption(openOpt);
                var aiEnhance = ctx.ParseResult.GetValueForOption(aiEnhanceOpt);
                var aiModel = ctx.ParseResult.GetValueForOption(aiModelOpt) ?? "llama3.2";

                // Try to load AI augmentations from YAML file (preferred)
                AiAugmentationManifest? augmentations = null;
                var augmentationsPath = Path.Combine(Path.GetDirectoryName(input.FullName)!, "ai-augmentations.yaml");
                if (File.Exists(augmentationsPath))
                {
                    try
                    {
                        Console.WriteLine($"📄 Loading AI augmentations from: {augmentationsPath}");
                        var yaml = await File.ReadAllTextAsync(augmentationsPath);
                        var deserializer = new DeserializerBuilder()
                            .WithNamingConvention(CamelCaseNamingConvention.Instance)
                            .Build();
                        augmentations = deserializer.Deserialize<AiAugmentationManifest>(yaml);
                        Console.WriteLine($"✔ Loaded {augmentations?.Augmentations.Values.Sum(list => list.Count) ?? 0} augmentations from {Path.GetFileName(augmentationsPath)}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"⚠️  Could not load augmentations: {ex.Message}");
                    }
                }

                if (outDir is null && zip is null)
                    throw new InvalidOperationException("You must specify either -o/--out <directory> or --zip <file>.");

                if (outDir is not null && zip is not null)
                    throw new InvalidOperationException("--zip is a substitute for -o/--out. Specify one or the other.");

                CliHelpers.EnsureFileExists(input, "Flight Plan YAML file");

                // Validate + build (compile YAML)
                var compiler = new FlightPlanCompiler();
                var yamlText = File.ReadAllText(input.FullName);
                var result = compiler.Compile(yamlText, Path.GetDirectoryName(input.FullName)!);

                CompilerCliHelpers.PrintDiagnostics(result);
                if (result.Errors.Any() || result.Plan is null)
                    Environment.Exit(1);

                var generatedToTempDir = false;
                string outDirPath;

                if (zip is not null)
                {
                    generatedToTempDir = true;
                    outDirPath = Path.Combine(Path.GetTempPath(), $"flightplan-publish-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(outDirPath);
                }
                else
                {
                    outDirPath = outDir!.FullName;
                    if (Directory.Exists(outDirPath))
                    {
                        if (!force)
                            throw new InvalidOperationException($"Output directory already exists: {outDirPath}. Use --overwrite (or --force) to overwrite.");

                        EnsureSafeToDeleteDirectory(outDirPath);
                        Directory.Delete(outDirPath, recursive: true);
                    }

                    Directory.CreateDirectory(outDirPath);
                }

                // Copy inputs for traceability
                var inputsDir = Path.Combine(outDirPath, "inputs");
                Directory.CreateDirectory(inputsDir);

                var copiedPlanPath = CopyFileToDirectory(input, inputsDir);

                // Copy YAML source files referenced by `uses` directly under inputs/
                // (preserving relative paths under the plan directory).
                var copiedSourcePaths = CopySourceFiles(result.Plan, input, inputsDir);

                // 2) Build output (compiled plan JSON)
                var compiledPath = Path.Combine(outDirPath, "flightplan.compiled.json");
                var compiledJson = JsonSerializer.Serialize(result.Plan, CompiledFlightPlanJson.CreateOptions());
                File.WriteAllText(compiledPath, compiledJson);

                // AI Enhancement setup
                AiReportEnhancer? enhancer = null;
                if (aiEnhance)
                {
                    Console.WriteLine($"🤖 AI enhancement enabled using {aiModel}");
                    enhancer = new AiReportEnhancer("http://localhost:11434", aiModel);
                }

                // 3) Reports (architecture + security)
                var renderer = CreateRenderer(format);
                var ext = GetReportExtension(format);

                var archReport = new ArchitectureOverviewReportGenerator().Generate(result.Plan);
                ReportTermHinting.AttachAndApply(archReport, result.Plan.Terms);
                if (enhancer != null)
                {
                    Console.WriteLine("   Enhancing Architecture Overview...");
                    await EnhanceReportAsync(archReport, result.Plan, "architecture", enhancer);
                }
                else if (augmentations != null)
                {
                    ApplyAugmentations(archReport, "architecture", augmentations);
                }
                var archPath = Path.Combine(outDirPath, $"architecture-overview.{ext}");
                File.WriteAllText(archPath, renderer.Render(archReport));

                var secReport = new SecurityOverviewReportGenerator().Generate(result.Plan);
                ReportTermHinting.AttachAndApply(secReport, result.Plan.Terms);
                if (enhancer != null)
                {
                    Console.WriteLine("   Enhancing Security Overview...");
                    await EnhanceReportAsync(secReport, result.Plan, "security", enhancer);
                }
                else if (augmentations != null)
                {
                    ApplyAugmentations(secReport, "security", augmentations);
                }
                var secPath = Path.Combine(outDirPath, $"security-overview.{ext}");
                File.WriteAllText(secPath, renderer.Render(secReport));

                var onboardingReport = new DeveloperOnboardingReportGenerator().Generate(result.Plan);
                ReportTermHinting.AttachAndApply(onboardingReport, result.Plan.Terms);
                if (enhancer != null)
                {
                    Console.WriteLine("   Enhancing Developer Onboarding...");
                    await EnhanceReportAsync(onboardingReport, result.Plan, "onboarding", enhancer);
                }
                else if (augmentations != null)
                {
                    ApplyAugmentations(onboardingReport, "onboarding", augmentations);
                }
                var onboardingPath = Path.Combine(outDirPath, $"developer-onboarding.{ext}");
                File.WriteAllText(onboardingPath, renderer.Render(onboardingReport));

                // Service catalog (split mode: index + one file per service)
                var serviceCatalogGenerator = new ServiceCatalogReportGenerator();
                
                var services = result.Plan.Entities.Services
                    .OrderBy(s => s.Name)
                    .ToList();

                var serviceDirName = "service-catalog";
                var serviceDirPath = Path.Combine(outDirPath, serviceDirName);
                Directory.CreateDirectory(serviceDirPath);
                var serviceCatalogPath = Path.Combine(serviceDirPath, $"index.{ext}");

                // Pre-compute stable, safe filenames for services.
                var serviceUsedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var fileNameByServiceId = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var svc in services)
                {
                    var key = string.IsNullOrWhiteSpace(svc.Id) ? svc.Name : svc.Id;
                    var baseName = CatalogLinkHelper.SanitizeFileName(string.IsNullOrWhiteSpace(key) ? svc.Name : key);
                    if (string.IsNullOrWhiteSpace(baseName)) baseName = "service";

                    var fileName = baseName;
                    var i = 2;
                    while (!serviceUsedNames.Add(fileName))
                    {
                        fileName = $"{baseName}-{i}";
                        i++;
                    }

                    if (!string.IsNullOrWhiteSpace(svc.Id))
                        fileNameByServiceId[svc.Id] = fileName;
                    else
                        fileNameByServiceId[svc.Name] = fileName;
                }

                // Index report links to per-service files.
                var serviceCatalogReport = serviceCatalogGenerator.GenerateIndex(result.Plan, svc =>
                {
                    var lookup = !string.IsNullOrWhiteSpace(svc.Id) ? svc.Id : svc.Name;
                    if (!fileNameByServiceId.TryGetValue(lookup, out var fn))
                        fn = CatalogLinkHelper.SanitizeFileName(lookup);

                    return $"{fn}.{ext}";
                });

                ReportTermHinting.AttachAndApply(serviceCatalogReport, result.Plan.Terms);
                File.WriteAllText(serviceCatalogPath, renderer.Render(serviceCatalogReport));

                // Per-service detail reports.
                foreach (var svc in services)
                {
                    var lookup = ! string.IsNullOrWhiteSpace(svc.Id) ? svc.Id : svc.Name;
                    var fn = fileNameByServiceId.TryGetValue(lookup, out var mapped) ? mapped : CatalogLinkHelper.SanitizeFileName(lookup);
                    var doc = serviceCatalogGenerator.GenerateServiceDetail(result.Plan, svc, indexHref: $"index.{ext}");
                    ReportTermHinting.AttachAndApply(doc, result.Plan.Terms);
                    var path = Path.Combine(serviceDirPath, $"{fn}.{ext}");
                    File.WriteAllText(path, renderer.Render(doc));
                }

                // Resource catalog (split mode: index + one file per resource)
                var resourceCatalogGenerator = new ResourceCatalogReportGenerator();
                
                var resources = result.Plan.Entities.Resources
                    .OrderBy(r => r.Name)
                    .ToList();

                var resourceDirName = "resource-catalog";
                var resourceDirPath = Path.Combine(outDirPath, resourceDirName);
                Directory.CreateDirectory(resourceDirPath);
                var resourceCatalogPath = Path.Combine(resourceDirPath, $"index.{ext}");

                // Pre-compute stable, safe filenames for resources.
                var resourceUsedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var fileNameByResourceId = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var res in resources)
                {
                    var key = string.IsNullOrWhiteSpace(res.Id) ? res.Name : res.Id;
                    var baseName = CatalogLinkHelper.SanitizeFileName(string.IsNullOrWhiteSpace(key) ? res.Name : key);
                    if (string.IsNullOrWhiteSpace(baseName)) baseName = "resource";

                    var fileName = baseName;
                    var i = 2;
                    while (!resourceUsedNames.Add(fileName))
                    {
                        fileName = $"{baseName}-{i}";
                        i++;
                    }

                    if (!string.IsNullOrWhiteSpace(res.Id))
                        fileNameByResourceId[res.Id] = fileName;
                    else
                        fileNameByResourceId[res.Name] = fileName;
                }

                // Index report links to per-resource files.
                var resourceCatalogReport = resourceCatalogGenerator.GenerateIndex(result.Plan, res =>
                {
                    var lookup = !string.IsNullOrWhiteSpace(res.Id) ? res.Id : res.Name;
                    if (!fileNameByResourceId.TryGetValue(lookup, out var fn))
                        fn = CatalogLinkHelper.SanitizeFileName(lookup);

                    return $"{fn}.{ext}";
                });

                ReportTermHinting.AttachAndApply(resourceCatalogReport, result.Plan.Terms);
                File.WriteAllText(resourceCatalogPath, renderer.Render(resourceCatalogReport));

                // Per-resource detail reports.
                foreach (var res in resources)
                {
                    var lookup = !string.IsNullOrWhiteSpace(res.Id) ? res.Id : res.Name;
                    var fn = fileNameByResourceId.TryGetValue(lookup, out var mapped) ? mapped : CatalogLinkHelper.SanitizeFileName(lookup);
                    var doc = resourceCatalogGenerator.GenerateResourceDetail(result.Plan, res, indexHref: $"index.{ext}");
                    ReportTermHinting.AttachAndApply(doc, result.Plan.Terms);
                    var path = Path.Combine(resourceDirPath, $"{fn}.{ext}");
                    File.WriteAllText(path, renderer.Render(doc));
                }

                // Aggregate findings
                var findings = new List<Finding>();
                findings.AddRange(archReport.Findings);
                findings.AddRange(secReport.Findings);
                findings.AddRange(onboardingReport.Findings);
                findings.AddRange(serviceCatalogReport.Findings);
                findings.AddRange(resourceCatalogReport.Findings);

                findings = FindingUtilities.NormalizeAndDeduplicate(findings);

                var findingsPath = Path.Combine(outDirPath, "findings.json");
                var findingsJson = JsonSerializer.Serialize(findings, CompiledFlightPlanJson.CreateOptions());
                File.WriteAllText(findingsPath, findingsJson);

                // Table of contents
                var toc = PublishedReportsTableOfContentsGenerator.Generate(
                    result.Plan,
                    format,
                    findings,
                    compiledFileName: Path.GetFileName(compiledPath),
                    findingsFileName: Path.GetFileName(findingsPath),
                    architectureFileName: Path.GetFileName(archPath),
                    securityFileName: Path.GetFileName(secPath),
                    onboardingFileName: Path.GetFileName(onboardingPath),
                    serviceCatalogFileName: $"service-catalog/index.{ext}",
                    resourceCatalogFileName: $"resource-catalog/index.{ext}");

                ReportTermHinting.AttachAndApply(toc, result.Plan.Terms);

                var tocPath = Path.Combine(outDirPath, $"index.{ext}");
				File.WriteAllText(tocPath, renderer.Render(toc));

				string? zipPath = null;
				if (zip is not null)
				{
					zipPath = Path.GetFullPath(zip.FullName);
					CreateZipArtifact(outDirPath, zipPath, overwrite: force);
				}

                if (open)
					TryOpenInDefaultBrowser(tocPath);

                // Save AI prompt log if AI enhancement was used
                if (enhancer != null && enhancer.GetPromptLog().Count > 0)
                {
                    var aiLogPath = Path.Combine(outDirPath, "ai-enhancements.log.json");
                    await enhancer.SavePromptLogAsync(aiLogPath);
                    Console.WriteLine($"📝 AI prompt log: {aiLogPath}");
                }

                Console.WriteLine("✔ Publish completed successfully.");
                Console.WriteLine($"✔ Inputs copied: {copiedPlanPath}");
                Console.WriteLine($"✔ Compiled plan: {compiledPath}");
                Console.WriteLine($"✔ Findings: {findingsPath}");
                Console.WriteLine($"✔ Architecture report: {archPath}");
                Console.WriteLine($"✔ Security report: {secPath}");
                Console.WriteLine($"✔ Onboarding report: {onboardingPath}");
                Console.WriteLine($"✔ Service catalog report: {serviceCatalogPath}");
                Console.WriteLine($"✔ Resource catalog report: {resourceCatalogPath}");
                Console.WriteLine($"✔ Table of contents: {tocPath}");
                if (zipPath is not null)
                    Console.WriteLine($"✔ Zip artifact: {zipPath}");
                if (!generatedToTempDir)
                    Console.WriteLine($"✔ Output directory: {outDirPath}");
                else if (open)
                    Console.WriteLine($"✔ Output directory: {outDirPath} (kept because --open was used)");

                if (generatedToTempDir && !open)
				{
					try
					{
						Directory.Delete(outDirPath, recursive: true);
                        Console.WriteLine("✔ Temporary output directory removed.");
					}
					catch (Exception ex)
					{
						Console.Error.WriteLine($"warning: Failed to delete temporary output directory: {ex.Message}");
					}
				}
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                Environment.Exit(1);
            }
        });
    }

    private static void CreateZipArtifact(string outDirPath, string zipPath, bool overwrite)
    {
        if (File.Exists(zipPath))
        {
            if (!overwrite)
                throw new InvalidOperationException($"Zip file already exists: {zipPath}. Use --overwrite (or --force) to overwrite.");
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(
            sourceDirectoryName: outDirPath,
            destinationArchiveFileName: zipPath,
            compressionLevel: CompressionLevel.Optimal,
            includeBaseDirectory: false);
    }

    private static void TryOpenInDefaultBrowser(string htmlPath)
    {
        try
        {
            var full = Path.GetFullPath(htmlPath);
            var psi = new ProcessStartInfo
            {
                FileName = full,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: Failed to open report in default browser: {ex.Message}");
        }
    }

    private static void EnsureSafeToDeleteDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new InvalidOperationException("Output directory path is empty.");

        var full = Path.GetFullPath(directoryPath);
        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrWhiteSpace(root))
        {
            var fullTrimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootTrimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullTrimmed, rootTrimmed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to overwrite root directory: {full}");
        }

        // Avoid accidental deletion of current working directory.
        var cwd = Directory.GetCurrentDirectory();
        if (string.Equals(Path.GetFullPath(cwd), full, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to overwrite the current working directory: {full}");
    }

    private static IReportDocumentRenderer CreateRenderer(string format)
    {
        if (format.Equals("html", StringComparison.OrdinalIgnoreCase))
            return new HtmlTailwindRenderer();

        return new MarkdownRenderer();
    }

    private static string GetReportExtension(string format)
    {
        return format.Equals("html", StringComparison.OrdinalIgnoreCase) ? "html" : "md";
    }

    private static string CopyFileToDirectory(FileInfo sourceFile, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        var fileName = Path.GetFileName(sourceFile.FullName);
        var destPath = Path.Combine(destinationDirectory, fileName);

        // Overwrite to allow repeatable publish runs into the same folder.
        File.Copy(sourceFile.FullName, destPath, overwrite: true);
        return destPath;
    }

    private static List<string> CopySourceFiles(CompiledFlightPlan plan, FileInfo input, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);

        var planDir = Path.GetDirectoryName(input.FullName) ?? Directory.GetCurrentDirectory();

        var inputFullPath = Path.GetFullPath(input.FullName);

        var specs = plan.Metadata?.SourceFiles?.ToList() ?? [];
        var absolutePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec))
                continue;

            // The compiler currently records "flightplan.yml" as a placeholder; map it to the actual input file.
            if (spec.Equals("flightplan.yml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var abs = Path.IsPathRooted(spec)
                ? Path.GetFullPath(spec)
                : Path.GetFullPath(Path.Combine(planDir, spec));

            absolutePaths.Add(abs);
        }

        var copied = new List<string>();
        foreach (var abs in absolutePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            // Root input YAML is already copied to inputs/flightplan.yml.
            if (string.Equals(Path.GetFullPath(abs), inputFullPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!File.Exists(abs))
            {
                Console.Error.WriteLine($"warning: referenced source file not found, skipping: {abs}");
                continue;
            }

            string relative;
            try
            {
                relative = Path.GetRelativePath(planDir, abs);
            }
            catch
            {
                relative = Path.GetFileName(abs);
            }

            // If the file is outside the plan directory, just copy by filename to avoid odd paths.
            if (relative.StartsWith("..", StringComparison.Ordinal))
                relative = Path.GetFileName(abs);

            var destPath = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(abs, destPath, overwrite: true);
            copied.Add(destPath);
        }

        return copied;
    }

    private static async Task EnhanceReportAsync(ReportDocument report, CompiledFlightPlan plan, string reportType, AiReportEnhancer enhancer)
    {
        try
        {
            // Enhance Executive Summary if it exists (match by anchor or heading)
            var execSection = report.Sections.FirstOrDefault(s => s.Anchor == "executive-summary") 
                ?? report.Sections.FirstOrDefault(s => s.Heading?.Equals("Executive Summary", StringComparison.OrdinalIgnoreCase) == true);
            
            if (execSection != null)
            {
                var summaryData = new Dictionary<string, string>
                {
                    ["Application"] = plan.Application?.Name ?? "N/A",
                    ["Type"] = plan.Application?.Type ?? "N/A",
                    ["Domain"] = plan.Application?.Domain ?? "N/A",
                    ["Services"] = plan.Entities.Services.Count.ToString(),
                    ["Resources"] = plan.Entities.Resources.Count.ToString(),
                    ["Platforms"] = plan.Entities.Platforms.Count().ToString(),
                    ["Environments"] = plan.Entities.Environments.Count.ToString()
                };
                
                var aiNarrative = await enhancer.GenerateExecutiveSummaryAsync(reportType, summaryData);
                if (!string.IsNullOrWhiteSpace(aiNarrative))
                {
                    execSection.Blocks.Insert(0, new ParagraphBlock 
                    { 
                        Text = aiNarrative,
                        Emphasis = TextEmphasis.None
                    });
                }
            }

            // Enhance key sections with insights
            await EnhanceSectionWithInsightsAsync(report, "platform-overview", enhancer, reportType, plan);
            await EnhanceSectionWithInsightsAsync(report, "service-overview", enhancer, reportType, plan);
            await EnhanceSectionWithInsightsAsync(report, "resource-overview", enhancer, reportType, plan);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠️  AI enhancement error: {ex.Message}");
            // Continue without enhancement rather than failing
        }
    }

    private static async Task EnhanceSectionWithInsightsAsync(
        ReportDocument report, 
        string sectionAnchor, 
        AiReportEnhancer enhancer, 
        string reportType,
        CompiledFlightPlan plan)
    {
        var section = report.Sections.FirstOrDefault(s => s.Anchor == sectionAnchor);
        if (section == null) return;

        var sectionData = BuildSectionContextData(sectionAnchor, plan);
        if (string.IsNullOrWhiteSpace(sectionData)) return;

        var insights = await enhancer.GenerateSectionInsightsAsync(reportType, section.Heading, sectionData);
        if (!string.IsNullOrWhiteSpace(insights))
        {
            section.Blocks.Insert(0, new CalloutBlock
            {
                CalloutType = CalloutKind.Info,
                Title = "AI Analysis",
                Message = insights
            });
        }
    }

    private static void ApplyAugmentations(ReportDocument report, string reportType, AiAugmentationManifest manifest)
    {
        if (!manifest.Augmentations.TryGetValue(reportType, out var augmentations))
            return;

        foreach (var augmentation in augmentations)
        {
            // Find section by anchor or heading
            var section = report.Sections.FirstOrDefault(s => s.Anchor == augmentation.SectionId)
                ?? report.Sections.FirstOrDefault(s => s.Heading?.Equals(augmentation.SectionId.Replace("-", " "), StringComparison.OrdinalIgnoreCase) == true);

            if (section == null) continue;

            if (augmentation.Type == "narrative")
            {
                // Insert as paragraph at beginning of section
                section.Blocks.Insert(0, new ParagraphBlock
                {
                    Text = augmentation.Content,
                    Emphasis = TextEmphasis.None
                });
            }
            else if (augmentation.Type == "callout")
            {
                // Insert as callout block
                section.Blocks.Insert(0, new CalloutBlock
                {
                    CalloutType = CalloutKind.Info,
                    Title = augmentation.Title ?? "AI Analysis",
                    Message = augmentation.Content
                });
            }
        }
    }

    private static string BuildSectionContextData(string sectionAnchor, CompiledFlightPlan plan)
    {
        return sectionAnchor switch
        {
            "platform-overview" => $"Platforms: {string.Join(", ", plan.Entities.Platforms.Select(p => p.Id))}. " +
                                   $"Total services: {plan.Entities.Services.Count}. " +
                                   $"Platform distribution: {string.Join(", ", plan.Entities.Services.GroupBy(s => s.PlatformRef).Select(g => $"{g.Key}={g.Count()}"))}",
            
            "service-overview" => $"Total services: {plan.Entities.Services.Count}. " +
                                  $"Teams: {string.Join(", ", plan.Entities.Services.GroupBy(s => s.OwnerRef).Select(g => $"{g.Key}={g.Count()}"))}. " +
                                  $"Zones: {string.Join(", ", plan.Entities.Services.GroupBy(s => s.ZoneRef).Select(g => $"{g.Key}={g.Count()}"))}",
            
            "resource-overview" => $"Total resources: {plan.Entities.Resources.Count}. " +
                                   $"Types: {string.Join(", ", plan.Entities.Resources.GroupBy(r => r.ResourceKind).Select(g => $"{g.Key}={g.Count()}"))}",
            
            _ => string.Empty
        };
    }
}
