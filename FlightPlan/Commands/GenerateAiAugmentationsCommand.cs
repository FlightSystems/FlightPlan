using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FlightPlan.Commands;

public static class GenerateAiAugmentationsCommand
{
    public static Command Create()
    {
        var command = new Command("ai-augmentations", "Generate AI augmentations for reports and save to YAML");

        var planFileArg = new Argument<FileInfo>(
            "plan",
            "Path to the compiled flight plan JSON file"
        );

        var outputOpt = new Option<FileInfo?>(
            ["--output", "-o"],
            "Output path for augmentations YAML (default: ai-augmentations.yaml in plan directory)"
        );

        var modelOpt = new Option<string>(
            "--model",
            getDefaultValue: () => "llama3.2",
            "AI model to use for generation"
        );

        var forceOpt = new Option<bool>(
            ["--force", "-f"],
            "Force regeneration even if plan hash hasn't changed"
        );

        command.AddArgument(planFileArg);
        command.AddOption(outputOpt);
        command.AddOption(modelOpt);
        command.AddOption(forceOpt);

        command.SetHandler(async (FileInfo planFile, FileInfo? output, string model, bool force) =>
        {
            await ExecuteAsync(planFile, output, model, force);
        }, planFileArg, outputOpt, modelOpt, forceOpt);

        return command;
    }

    private static async Task ExecuteAsync(FileInfo planFile, FileInfo? outputFile, string model, bool force)
    {
        if (!planFile.Exists)
        {
            Console.Error.WriteLine($"❌ Plan file not found: {planFile.FullName}");
            Environment.ExitCode = 1;
            return;
        }

        // Default output path
        if (outputFile == null)
        {
            var planDir = Path.GetDirectoryName(planFile.FullName) ?? Directory.GetCurrentDirectory();
            outputFile = new FileInfo(Path.Combine(planDir, "ai-augmentations.yaml"));
        }

        Console.WriteLine($"📋 Loading plan: {planFile.FullName}");
        var planJson = await File.ReadAllTextAsync(planFile.FullName);
        var plan = JsonSerializer.Deserialize<CompiledFlightPlan>(planJson, CompiledFlightPlanJson.CreateOptions());
        
        if (plan == null)
        {
            Console.Error.WriteLine("❌ Failed to parse compiled plan");
            Environment.ExitCode = 1;
            return;
        }

        // Calculate plan hash
        var planHash = ComputeHash(planJson);
        Console.WriteLine($"📊 Plan hash: {planHash[..12]}...");

        // Check if augmentations already exist and are up-to-date
        AiAugmentationManifest? existingManifest = null;
        if (outputFile.Exists && !force)
        {
            try
            {
                var existingYaml = await File.ReadAllTextAsync(outputFile.FullName);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .Build();
                existingManifest = deserializer.Deserialize<AiAugmentationManifest>(existingYaml);

                if (existingManifest?.PlanHash == planHash)
                {
                    Console.WriteLine("✅ Augmentations are already up-to-date (plan hash matches)");
                    Console.WriteLine($"   Use --force to regenerate anyway");
                    return;
                }
                else
                {
                    Console.WriteLine("🔄 Plan has changed, regenerating augmentations...");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Could not read existing augmentations: {ex.Message}");
            }
        }

        // Generate augmentations
        Console.WriteLine($"🤖 Generating AI augmentations using {model}...");
        var enhancer = new AiReportEnhancer("http://localhost:11434", model);
        var manifest = new AiAugmentationManifest
        {
            Generated = DateTime.UtcNow,
            Model = model,
            PlanHash = planHash,
            Augmentations = new Dictionary<string, List<AiAugmentation>>()
        };

        // Generate augmentations for each report type
        await GenerateForArchitecture(plan, enhancer, manifest);
        await GenerateForSecurity(plan, enhancer, manifest);
        await GenerateForOnboarding(plan, enhancer, manifest);

        // Save to YAML
        Console.WriteLine($"💾 Saving augmentations to: {outputFile.FullName}");
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var yaml = serializer.Serialize(manifest);
        await File.WriteAllTextAsync(outputFile.FullName, yaml);

        var totalCount = manifest.Augmentations.Values.Sum(list => list.Count);
        Console.WriteLine($"✔ Generated {totalCount} augmentations across {manifest.Augmentations.Count} reports");
        
        // Save prompt log
        var logPath = Path.Combine(Path.GetDirectoryName(outputFile.FullName)!, "ai-augmentations.log.json");
        await enhancer.SavePromptLogAsync(logPath);
        Console.WriteLine($"📝 AI prompt log: {logPath}");
    }

    private static async Task GenerateForArchitecture(CompiledFlightPlan plan, AiReportEnhancer enhancer, AiAugmentationManifest manifest)
    {
        Console.WriteLine("   Generating for Architecture Overview...");
        var augmentations = new List<AiAugmentation>();

        // Executive summary
        var summaryData = BuildSummaryData(plan);
        var execSummary = await enhancer.GenerateExecutiveSummaryAsync("architecture", summaryData);
        if (!string.IsNullOrWhiteSpace(execSummary))
        {
            augmentations.Add(new AiAugmentation
            {
                SectionId = "executive-summary",
                Content = execSummary,
                Type = "narrative"
            });
        }

        // Platform overview insights
        var platformData = $"Platforms: {string.Join(", ", plan.Entities.Platforms.Select(p => p.Id))}. " +
                          $"Total services: {plan.Entities.Services.Count}. " +
                          $"Platform distribution: {string.Join(", ", plan.Entities.Services.GroupBy(s => s.PlatformRef ?? "").Select(g => $"{g.Key}={g.Count()}"))}";
        
        var platformInsights = await enhancer.GenerateSectionInsightsAsync("architecture", "Platform Overview", platformData);
        if (!string.IsNullOrWhiteSpace(platformInsights))
        {
            augmentations.Add(new AiAugmentation
            {
                SectionId = "platform-overview",
                Title = "AI Analysis",
                Content = platformInsights,
                Type = "callout"
            });
        }

        // Service overview insights
        var serviceData = $"Total services: {plan.Entities.Services.Count}. " +
                         $"Teams: {string.Join(", ", plan.Entities.Services.GroupBy(s => s.OwnerRef ?? "").Select(g => $"{g.Key}={g.Count()}"))}. " +
                         $"Zones: {string.Join(", ", plan.Entities.Services.GroupBy(s => s.ZoneRef ?? "").Select(g => $"{g.Key}={g.Count()}"))}";
        
        var serviceInsights = await enhancer.GenerateSectionInsightsAsync("architecture", "Service Overview", serviceData);
        if (!string.IsNullOrWhiteSpace(serviceInsights))
        {
            augmentations.Add(new AiAugmentation
            {
                SectionId = "service-overview",
                Title = "AI Analysis",
                Content = serviceInsights,
                Type = "callout"
            });
        }

        // Resource overview insights
        var resourceData = $"Total resources: {plan.Entities.Resources.Count}. " +
                          $"Types: {string.Join(", ", plan.Entities.Resources.GroupBy(r => r.ResourceKind ?? "").Select(g => $"{g.Key}={g.Count()}"))}";
        
        var resourceInsights = await enhancer.GenerateSectionInsightsAsync("architecture", "Resource Overview", resourceData);
        if (!string.IsNullOrWhiteSpace(resourceInsights))
        {
            augmentations.Add(new AiAugmentation
            {
                SectionId = "resource-overview",
                Title = "AI Analysis",
                Content = resourceInsights,
                Type = "callout"
            });
        }

        manifest.Augmentations["architecture"] = augmentations;
    }

    private static async Task GenerateForSecurity(CompiledFlightPlan plan, AiReportEnhancer enhancer, AiAugmentationManifest manifest)
    {
        Console.WriteLine("   Generating for Security Overview...");
        var augmentations = new List<AiAugmentation>();

        var summaryData = BuildSummaryData(plan);
        var execSummary = await enhancer.GenerateExecutiveSummaryAsync("security", summaryData);
        if (!string.IsNullOrWhiteSpace(execSummary))
        {
            augmentations.Add(new AiAugmentation
            {
                SectionId = "executive-summary",
                Content = execSummary,
                Type = "narrative"
            });
        }

        manifest.Augmentations["security"] = augmentations;
    }

    private static async Task GenerateForOnboarding(CompiledFlightPlan plan, AiReportEnhancer enhancer, AiAugmentationManifest manifest)
    {
        Console.WriteLine("   Generating for Developer Onboarding...");
        var augmentations = new List<AiAugmentation>();

        // System at a glance insights
        var summaryData = BuildSummaryData(plan);
        var systemGlanceInsights = await enhancer.GenerateSectionInsightsAsync(
            "onboarding", 
            "System at a Glance",
            $"Application: {summaryData["Application"]}, Type: {summaryData["Type"]}, Domain: {summaryData["Domain"]}, " +
            $"Services: {summaryData["Services"]}, Resources: {summaryData["Resources"]}, " +
            $"Platforms: {summaryData["Platforms"]}, Environments: {summaryData["Environments"]}"
        );
        if (!string.IsNullOrWhiteSpace(systemGlanceInsights))
        {
            augmentations.Add(new AiAugmentation
            {
                SectionId = "system-at-a-glance",
                Title = "AI Insights",
                Content = systemGlanceInsights,
                Type = "callout"
            });
        }

        // Tech stack insights
        var platformsByService = plan.Entities.Services.GroupBy(s => s.PlatformRef ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var techStackData = $"Technology distribution: {string.Join(", ", platformsByService.Select(kv => $"{kv.Key}={kv.Value}"))}. " +
                           $"Total platforms: {plan.Entities.Platforms.Count()}. " +
                           $"Total services: {plan.Entities.Services.Count}";
        
        var techStackInsights = await enhancer.GenerateSectionInsightsAsync("onboarding", "Tech Stack & Runtimes", techStackData);
        if (!string.IsNullOrWhiteSpace(techStackInsights))
        {
            augmentations.Add(new AiAugmentation
            {
                SectionId = "step-5-tech-stack",
                Title = "AI Insights",
                Content = techStackInsights,
                Type = "callout"
            });
        }

        // Data & State insights
        var resourcesByType = plan.Entities.Resources.GroupBy(r => r.ResourceKind ?? "unknown").ToDictionary(g => g.Key, g => g.Count());
        var dataStateData = $"Resource distribution: {string.Join(", ", resourcesByType.Select(kv => $"{kv.Key}={kv.Value}"))}. " +
                           $"Total resources: {plan.Entities.Resources.Count}";
        
        var dataStateInsights = await enhancer.GenerateSectionInsightsAsync("onboarding", "Data & State", dataStateData);
        if (!string.IsNullOrWhiteSpace(dataStateInsights))
        {
            augmentations.Add(new AiAugmentation
            {
                SectionId = "step-6-data-state",
                Title = "AI Insights",
                Content = dataStateInsights,
                Type = "callout"
            });
        }

        // Deployment insights
        var deploymentData = $"Environments: {plan.Entities.Environments.Count}. " +
                            $"Environment flow: {string.Join(" → ", plan.Entities.Environments.Select(e => e.Name ?? e.Id))}";
        
        var deploymentInsights = await enhancer.GenerateSectionInsightsAsync("onboarding", "How Code Gets to Production", deploymentData);
        if (!string.IsNullOrWhiteSpace(deploymentInsights))
        {
            augmentations.Add(new AiAugmentation
            {
                SectionId = "step-4-code-to-prod",
                Title = "AI Insights",
                Content = deploymentInsights,
                Type = "callout"
            });
        }

        manifest.Augmentations["onboarding"] = augmentations;
    }

    private static Dictionary<string, string> BuildSummaryData(CompiledFlightPlan plan)
    {
        return new Dictionary<string, string>
        {
            ["Application"] = plan.Application?.Name ?? "N/A",
            ["Type"] = plan.Application?.Type ?? "N/A",
            ["Domain"] = plan.Application?.Domain ?? "N/A",
            ["Services"] = plan.Entities.Services.Count.ToString(),
            ["Resources"] = plan.Entities.Resources.Count.ToString(),
            ["Platforms"] = plan.Entities.Platforms.Count().ToString(),
            ["Environments"] = plan.Entities.Environments.Count.ToString()
        };
    }

    private static string ComputeHash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
