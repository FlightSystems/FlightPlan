using System.CommandLine;
using System.CommandLine.Invocation;
using FlightPlan.Reporting;

namespace FlightPlan.Commands;

internal static class ReportCommand
{
    internal static void Configure(
        Command reportCmd,
        Argument<FileInfo> inputArg,
        Option<string> formatOpt,
        Option<string> typeOpt,
        Option<FileInfo?> outputOpt,
        Option<bool> overwriteOpt,
        Option<bool> aiEnhanceOpt,
        Option<string> aiModelOpt,
        Option<FileInfo?> aiLogOpt,
        Option<bool> showEmptyOpt)
    {
        reportCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var input = ctx.ParseResult.GetValueForArgument(inputArg);
            var format = ctx.ParseResult.GetValueForOption(formatOpt)!;
            var reportType = ctx.ParseResult.GetValueForOption(typeOpt)!;
            var output = ctx.ParseResult.GetValueForOption(outputOpt);
            var overwrite = ctx.ParseResult.GetValueForOption(overwriteOpt);
            var aiEnhance = ctx.ParseResult.GetValueForOption(aiEnhanceOpt);
            var aiModel = ctx.ParseResult.GetValueForOption(aiModelOpt)!;
            var aiLog = ctx.ParseResult.GetValueForOption(aiLogOpt);
            var showEmpty = ctx.ParseResult.GetValueForOption(showEmptyOpt);
            
            CliHelpers.EnsureFileExists(input, "Flight Plan YAML file");

            var compiler = new FlightPlanCompiler();
            var yamlText = File.ReadAllText(input.FullName);
            var result = compiler.Compile(yamlText, Path.GetDirectoryName(input.FullName)!);

            CompilerCliHelpers.PrintDiagnostics(result);

            if (result.Errors.Any())
                Environment.Exit(1);

            string reportTitle;
            IReportGenerator generator;
            switch (reportType.ToLowerInvariant())
            {
                case "architecture":
                case "ar":
                    reportTitle = "Architecture Overview Report";
                    generator = new ArchitectureOverviewReportGenerator();
                    break;

                case "security":
                case "sr":
                    reportTitle = "Security Overview Report";
                    generator = new SecurityOverviewReportGenerator();
                    break;

                case "onboarding":
                case "dev":
                case "developer":
                    reportTitle = "Developer Onboarding Report";
                    generator = new DeveloperOnboardingReportGenerator();
                    break;

                case "service-catalog":
                case "catalog":
                case "services":
                case "sc":
                    reportTitle = "Service Catalog Report";
                    generator = new ServiceCatalogReportGenerator();
                    break;

                case "resource-catalog":
                case "resources":
                case "rc":
                    reportTitle = "Resource Catalog Report";
                    generator = new ResourceCatalogReportGenerator();
                    break;

                default:
                    Console.Error.WriteLine($"error: Unknown report type '{reportType}'.");
                    Environment.Exit(1);
                    return;
            }

            var options = new ReportGenerationOptions { ShowEmptySections = showEmpty };
            var report = generator.Generate(result.Plan!, options);
            ReportTermHinting.AttachAndApply(report, result.Plan!.Terms);

            // AI Enhancement
            AiReportEnhancer? enhancer = null;
            if (aiEnhance)
            {
                Console.WriteLine($"🤖 Enhancing report with AI narratives using {aiModel}...");
                enhancer = new AiReportEnhancer("http://localhost:11434", aiModel);
                
                try
                {
                    await EnhanceReportWithAiAsync(report, result.Plan!, reportType, enhancer);
                    Console.WriteLine("✔ AI enhancement complete");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"⚠️  AI enhancement failed: {ex.Message}");
                    Console.Error.WriteLine("   Report will be generated without AI enhancements.");
                }
            }

            IReportDocumentRenderer renderer;
            if (format.Equals("html", StringComparison.CurrentCultureIgnoreCase))
                renderer = new HtmlTailwindRenderer();
            else
                renderer = new MarkdownRenderer();

            var reportOutput = renderer.Render(report);

            if (output != null)
            {
                CliHelpers.WriteOutputTextFile(output, reportOutput, overwrite);
                Console.WriteLine($"✔ {reportTitle} saved to {output.FullName} ({format} format).");
                
                // Save AI prompt log if AI was used
                if (enhancer != null && enhancer.GetPromptLog().Count > 0)
                {
                    var logPath = aiLog?.FullName ?? Path.ChangeExtension(output.FullName, ".ai-log.json");
                    await enhancer.SavePromptLogAsync(logPath);
                    Console.WriteLine($"📝 AI prompt log saved to {logPath}");
                }
            }
            else
            {
                Console.WriteLine(reportOutput);
                Console.WriteLine($"✔ {reportTitle} generated ({format} format).");
                
                // Save AI prompt log to default location if AI was used
                if (enhancer != null && enhancer.GetPromptLog().Count > 0)
                {
                    var logPath = aiLog?.FullName ?? $"{reportType}-report.ai-log.json";
                    await enhancer.SavePromptLogAsync(logPath);
                    Console.WriteLine($"📝 AI prompt log saved to {logPath}");
                }
            }
        });
    }

    private static async Task EnhanceReportWithAiAsync(ReportDocument report, CompiledFlightPlan plan, string reportType, AiReportEnhancer enhancer)
    {
        // Enhance Executive Summary if it exists
        var execSection = report.Sections.FirstOrDefault(s => s.Anchor == "executive-summary");
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
                // Insert AI-generated narrative after existing summary blocks
                execSection.Blocks.Insert(0, new ParagraphBlock 
                { 
                    Text = aiNarrative,
                    Emphasis = TextEmphasis.None
                });
            }
        }

        // Enhance other key sections with insights
        await EnhanceSectionWithInsightsAsync(report, "platform-overview", enhancer, reportType, plan);
        await EnhanceSectionWithInsightsAsync(report, "service-overview", enhancer, reportType, plan);
        await EnhanceSectionWithInsightsAsync(report, "resource-overview", enhancer, reportType, plan);
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

        // Build context data for the section
        var sectionData = BuildSectionContextData(sectionAnchor, plan);
        if (string.IsNullOrWhiteSpace(sectionData)) return;

        var insights = await enhancer.GenerateSectionInsightsAsync(reportType, section.Heading, sectionData);
        if (!string.IsNullOrWhiteSpace(insights))
        {
            // Add insights as a callout at the beginning of the section
            section.Blocks.Insert(0, new CalloutBlock
            {
                CalloutType = CalloutKind.Info,
                Title = "AI Analysis",
                Message = insights
            });
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

