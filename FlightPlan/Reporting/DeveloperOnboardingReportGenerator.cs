namespace FlightPlan.Reporting;

/// <summary>
/// Produces a developer-oriented onboarding report from a validated CompiledFlightPlan.
/// Focuses on orientation: entry points, ownership, code locations, environments, and deployment processes.
/// </summary>
public sealed class DeveloperOnboardingReportGenerator : IReportGenerator
{
    private ReportGenerationOptions _options = new();
    
    // Published output names (PublishCommand writes these exact filenames).
    // Links use .md extension for markdown compatibility; HTML rendering will need URL rewriting or extension handling.
    private const string TocFile = "index.md";
    private const string ArchitectureReportFile = "architecture-overview.md";
    private const string SecurityReportFile = "security-overview.md";
    private const string ServiceCatalogReportFile = "service-catalog/index.md";
    private const string AlignmentReportFile = "deployment-alignment.md";
    private const string InputsPlanFile = "inputs/flightplan.yaml";
    private const string CompiledPlanFile = "flightplan.compiled.json";

    public ReportDocument Generate(CompiledFlightPlan plan)
    {
        return Generate(plan, new ReportGenerationOptions());
    }
    
    public ReportDocument Generate(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        _options = options ?? new ReportGenerationOptions();

        var doc = new ReportDocument
        {
            Title = "Developer Onboarding",
            Subtitle = "Step-by-step orientation guide for engineers new to the system",
            Metadata = new ReportMetadata
            {
                ApplicationName = plan.Application?.Name,
                Version = plan.Metadata.Format,
                Tags =
                {
                    ["report"] = "developer-onboarding",
                    ["generated-by"] = "flightplan"
                }
            }
        };

        doc.Sections.Add(BuildStartHere(plan));
        doc.Sections.Add(BuildSystemAtAGlance(plan));
        //doc.Sections.Add(BuildStepMap());
        //doc.Sections.Add(BuildStep1_SystemOverview(plan));
        doc.Sections.Add(BuildStep2_Architecture(plan));
        doc.Sections.Add(BuildStep3_SourceControlAndRepos(plan));
        doc.Sections.Add(BuildStep4_CodeToProduction(plan));
        doc.Sections.Add(BuildStep5_TechStackAndRuntimes(plan));
        doc.Sections.Add(BuildStep6_DataAndState(plan));
        doc.Sections.Add(BuildStep7_ConfigSecretsEnvironments(plan));
        doc.Sections.Add(BuildStep8_ObservabilityAndDebugging(plan));
        doc.Sections.Add(BuildStep9_SecurityAndAccess(plan));
        doc.Sections.Add(BuildWorkingWithFlightPlan(plan));

        return doc;
    }

    private static ReportSection BuildStartHere(CompiledFlightPlan plan)
    {
        return new ReportSection
        {
            Heading = "Start here (10–15 minutes)",
            Level = 1,
            Anchor = "start-here",
            Blocks =
            {
                new CalloutBlock
                {
                    CalloutType = CalloutKind.Info,
                    Title = "Goal",
                    Message =
                        "This report is a quick orientation checklist. It intentionally links out to the deeper reports " +
                        "instead of duplicating full inventories."
                },
                new BulletListBlock
                {
                    Items =
                    {
                        new ListItem { Text = $"Open the {Link(TocFile, "published table of contents")} and skim the report list." },
                        new ListItem { Text = $"Use the {Link("#steps", "step map")} below to navigate by topic (architecture, repos, deployments, security, etc)." },
                        new ListItem { Text = $"Read {Link(ArchitectureReportFile, "Architecture Overview")} for system context, toolchain, and operational readiness." },
                        new ListItem { Text = $"Scan {Link(ServiceCatalogReportFile + "#entry-points", "Entry Points")} to see the front doors, auth, and data contracts." },
                        new ListItem { Text = $"Use {Link(ServiceCatalogReportFile, "Service Catalog")} to jump to repos and owners (and per-service dependencies)." },
                        new ListItem { Text = $"If you’re shipping changes, review {Link(AlignmentReportFile, "Deployment Alignment")} for intent vs deployed reality." },
                        new ListItem { Text = $"For security review context, start with {Link(SecurityReportFile, "Security Overview")} (exposure + trust boundaries)." }
                    }
                }
            }
        };
    }

    private static ReportSection BuildStepMap()
    {
        return new ReportSection
        {
            Heading = "Steps (quick map)",
            Level = 1,
            Anchor = "steps",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text = "Use these steps to orient yourself quickly. Each step summarizes signals and links to the deeper reports."
                },
                new BulletListBlock
                {
                    Items =
                    {
                        new ListItem { Text = Link("#step-1-system-overview", "System Overview") },
                        new ListItem { Text = Link("#step-2-architecture", "Architecture") },
                        new ListItem { Text = Link("#step-3-source-control-repos", "Source Control & Repos") },
                        new ListItem { Text = Link("#step-4-code-to-prod", "How Code Gets to Production") },
                        new ListItem { Text = Link("#step-5-tech-stack", "Tech Stack & Runtimes") },
                        new ListItem { Text = Link("#step-6-data-state", "Data & State") },
                        new ListItem { Text = Link("#step-7-config-secrets", "Config, Secrets & Environments") },
                        new ListItem { Text = Link("#step-8-observability", "Observability & Debugging") },
                        new ListItem { Text = Link("#step-9-security", "Security & Access Basics") }
                    }
                }
            }
        };
    }

    private ReportSection BuildSystemAtAGlance(CompiledFlightPlan plan)
    {
        var entryExports = GetEntryExports(plan);
        var terminalEntryExports = FilterToTerminalEntryExports(plan, entryExports);
        var entryAuth = terminalEntryExports
            .Select(e => (e.Auth ?? string.Empty).Trim())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a)
            .ToList();

        var kv = new KeyValueTableBlock();
        
        if (!string.IsNullOrWhiteSpace(plan.Application?.Name))
            kv.Rows.Add(kv.New("Application", plan.Application.Name));
        else if (_options.ShowEmptySections)
            kv.Rows.Add(kv.New("Application", "Not modeled yet"));
        
        if (!string.IsNullOrWhiteSpace(plan.Application?.Type))
            kv.Rows.Add(kv.New("Type", plan.Application.Type));
        else if (_options.ShowEmptySections)
            kv.Rows.Add(kv.New("Type", "Not modeled yet"));
        
        if (!string.IsNullOrWhiteSpace(plan.Application?.Domain))
            kv.Rows.Add(kv.New("Domain", plan.Application.Domain));
        else if (_options.ShowEmptySections)
            kv.Rows.Add(kv.New("Domain", "Not modeled yet"));
        
        if (!string.IsNullOrWhiteSpace(plan.DeliveryModel?.Hosting))
            kv.Rows.Add(kv.New("Hosting", plan.DeliveryModel.Hosting));
        else if (_options.ShowEmptySections)
            kv.Rows.Add(kv.New("Hosting", "Not modeled yet"));
        
        if (!string.IsNullOrWhiteSpace(plan.DeliveryModel?.ServiceModel))
            kv.Rows.Add(kv.New("Service Model", plan.DeliveryModel.ServiceModel));
        else if (_options.ShowEmptySections)
            kv.Rows.Add(kv.New("Service Model", "Not modeled yet"));
        
        if (!string.IsNullOrWhiteSpace(plan.DeliveryModel?.Tenancy))
            kv.Rows.Add(kv.New("Tenancy", plan.DeliveryModel.Tenancy));
        else if (_options.ShowEmptySections)
            kv.Rows.Add(kv.New("Tenancy", "Not modeled yet"));
        
        if (plan.DeliveryModel?.PrimaryUsers is { Count: > 0 })
        {
            var primaryUsers = string.Join(", ", plan.DeliveryModel.PrimaryUsers
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(u => u));
            if (!string.IsNullOrWhiteSpace(primaryUsers))
                kv.Rows.Add(kv.New("Primary Users", primaryUsers));
            else if (_options.ShowEmptySections)
                kv.Rows.Add(kv.New("Primary Users", "Not modeled yet"));
        }
        else if (_options.ShowEmptySections)
            kv.Rows.Add(kv.New("Primary Users", "Not modeled yet"));
        
        kv.Rows.Add(kv.New("Services", plan.Entities.Services.Count.ToString()));
        kv.Rows.Add(kv.New("Resources", plan.Entities.Resources.Count.ToString()));
        kv.Rows.Add(kv.New("Environments", plan.Entities.Environments.Count.ToString()));
        kv.Rows.Add(kv.New("Zones", plan.Entities.Zones.Count.ToString()));
        kv.Rows.Add(kv.New("Entry Points (terminal)", terminalEntryExports.Count.ToString()));
        
        if (entryAuth.Count > 0)
            kv.Rows.Add(kv.New("Entry Auth (distinct)", string.Join(", ", entryAuth)));
        else if (_options.ShowEmptySections)
            kv.Rows.Add(kv.New("Entry Auth (distinct)", "Not modeled yet"));

        return new ReportSection
        {
            Heading = "System at a glance",
            Level = 1,
            Anchor = "system-at-a-glance",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This is a quick summary pulled from the compiled model. Use the links in the next section to get to the right place fast."
                },
                kv
            }
        };
    }

    private static ReportSection BuildStep1_SystemOverview(CompiledFlightPlan plan)
    {
        return new ReportSection
        {
            Heading = "Step 1 — System Overview",
            Level = 1,
            Anchor = "step-1-system-overview",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text = "Start with the system shape: what it is, who it serves, where it runs, and what the front doors are."
                },
                new BulletListBlock
                {
                    Items =
                    {
                        new ListItem { Text = $"{Link("#system-at-a-glance", "System at a glance")}: fast counts + entry-point auth signals." },
                        new ListItem { Text = $"{Link(ArchitectureReportFile + "#system-context", "Architecture — System Context")}: external actors + trust zones." },
                        new ListItem { Text = $"{Link(ServiceCatalogReportFile + "#entry-points", "Service Catalog — Entry Points")}: the front doors (contracts + auth)." },
                        new ListItem { Text = $"{Link(InputsPlanFile, "Source")}: application + deliveryModel + annotations." },
                    }
                }
            }
        };
    }

    private static ReportSection BuildStep2_Architecture(CompiledFlightPlan plan)
    {
        var viewDiagrams = GetAnnotationMapList(plan, "architectureViews");
        var legacyViews = GetAnnotationMap(plan, "architectureViews");

        var items = new List<ListItem>
        {
            new() { Text = $"{Link(ArchitectureReportFile, "Architecture Overview")}: the canonical narrative + diagrams/links." },
            new() { Text = $"{Link(ArchitectureReportFile + "#ownership-support", "Ownership + Support")}: escalation + coverage." },
            new() { Text = $"{Link(ArchitectureReportFile + "#toolchain-overview", "Toolchain Overview")}: repo/build/deploy/work tracking defaults." }
        };

        if (viewDiagrams is { Count: > 0 })
        {
            foreach (var diagram in viewDiagrams.Take(6))
            {
                diagram.TryGetValue("name", out var nameObj);
                diagram.TryGetValue("description", out var descObj);
                diagram.TryGetValue("url", out var urlObj);

                var name = (nameObj?.ToString() ?? string.Empty).Trim();
                var desc = (descObj?.ToString() ?? string.Empty).Trim();
                var url = (urlObj?.ToString() ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(url))
                    continue;

                var label = string.IsNullOrWhiteSpace(name) ? "diagram" : name;
                var link = string.IsNullOrWhiteSpace(url) ? label : Link(url, label);
                items.Add(new ListItem { Text = string.IsNullOrWhiteSpace(desc) ? link : $"{link}: {desc}" });
            }
        }
        else if (legacyViews is { Count: > 0 })
        {
            foreach (var li in FormatAnnotationMapItems(
                         legacyViews,
                         maxItems: 6,
                         sourcePrefix: "architectureViews",
                         sourceLink: InputsPlanFile))
            {
                items.Add(li);
            }
        }
        else
        {
            items.Add(new ListItem
            {
                Text = $"{Link(InputsPlanFile, "Source")}: add annotations.architectureViews as a list of diagram links for instant orientation."
            });
        }

        return new ReportSection
        {
            Heading = "Architecture",
            Level = 1,
            Anchor = "step-2-architecture",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text = "Focus on the mental model: boundaries, diagrams, ownership, and what ‘good’ looks like operationally."
                },
                new BulletListBlock { Items = items }
            }
        };
    }

    private static ReportSection BuildStep3_SourceControlAndRepos(CompiledFlightPlan plan)
    {
        var repoUrls = plan.Entities.Services
            .Select(s => (s.RepoUrl ?? string.Empty).Trim())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var servicesWithRepo = plan.Entities.Services.Count(s => !string.IsNullOrWhiteSpace(s.RepoUrl));
        var hosts = repoUrls
            .Select(TryGetHost)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h)
            .ToList();

        var sourceControl = GetAnnotationMap(plan, "engineering", "source_control") ?? GetAnnotationMap(plan, "engineering", "sourceControl");

        var items = new List<ListItem>
        {
            new() { Text = $"{Link(ServiceCatalogReportFile, "Service Catalog")}: jump to per-service repos." },
            new() { Text = $"{Link(CompiledPlanFile, "Generated")}: services[*].repoUrl present for {servicesWithRepo}/{plan.Entities.Services.Count} service(s) ({repoUrls.Count} distinct repo URL(s))." }
        };

        if (hosts.Count > 0)
        {
            items.Add(new ListItem
            {
                Text = $"{Link(CompiledPlanFile, "Generated")}: repo host(s) detected: {string.Join(", ", hosts)}."
            });
        }

        var repoTool = GetToolingFromDefaultToolchain(plan, "repo");
        if (!string.IsNullOrWhiteSpace(repoTool.linkText))
        {
            items.Add(new ListItem
            {
                Text = $"{Link(ArchitectureReportFile + "#toolchain-overview", "Default repo system")}: {repoTool.linkText}."
            });
        }

        if (sourceControl is { Count: > 0 })
        {
            items.AddRange(FormatAnnotationMapItems(sourceControl, maxItems: 5, sourcePrefix: "engineering.source_control", sourceLink: InputsPlanFile));
        }
        else
        {
            items.Add(new ListItem
            {
                Text = $"{Link(InputsPlanFile, "Source")}: (optional) add annotations.engineering.source_control (branching model, PR policy, required checks)."
            });
        }

        return new ReportSection
        {
            Heading = "Source Control & Repos",
            Level = 1,
            Anchor = "step-3-source-control-repos",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text = "Figure out where code lives, how it’s reviewed, and how to find the repo for any service quickly."
                },
                new BulletListBlock { Items = items }
            }
        };
    }

    private static ReportSection BuildStep4_CodeToProduction(CompiledFlightPlan plan)
    {
        var deployment = GetAnnotationMap(plan, "deployment") ?? GetAnnotationMap(plan, "annotations", "deployment");
        var envFlow = DescribeEnvironmentFlow(plan);

        var buildTool = GetToolingFromDefaultToolchain(plan, "build");
        var deployTool = GetToolingFromDefaultToolchain(plan, "deploy");

        var items = new List<ListItem>
        {
            new() { Text = $"{Link(AlignmentReportFile, "Deployment Alignment")}: intent vs deployed reality (use before large changes)." },
            new() { Text = $"{Link(ArchitectureReportFile + "#deployment-delivery", "Architecture — Deployment + Delivery")}: deployment topology + rollout/rollback." },
            new() { Text = $"{Link(ArchitectureReportFile + "#environment-overview", "Architecture — Environment Overview")}: promotion flow + environment intent." },
            new() { Text = $"{Link(InputsPlanFile, "Source")}: annotations.deployment.* (strategy/policy notes)." }
        };

        if (!string.IsNullOrWhiteSpace(buildTool.linkText))
            items.Add(new ListItem { Text = $"{Link(ArchitectureReportFile + "#toolchain-overview", "CI/build")}: {buildTool.linkText}." });
        if (!string.IsNullOrWhiteSpace(deployTool.linkText))
            items.Add(new ListItem { Text = $"{Link(ArchitectureReportFile + "#toolchain-overview", "CD/deploy")}: {deployTool.linkText}." });

        if (!string.IsNullOrWhiteSpace(envFlow))
            items.Add(new ListItem { Text = $"{Link(ArchitectureReportFile + "#environment-overview", "Environment flow")}: {envFlow}." });

        if (deployment is { Count: > 0 })
            items.AddRange(FormatAnnotationMapItems(deployment, maxItems: 6, sourcePrefix: "deployment", sourceLink: InputsPlanFile));

        return new ReportSection
        {
            Heading = "How Code Gets to Production",
            Level = 1,
            Anchor = "step-4-code-to-prod",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text = "Understand the pipeline, the environments, and the release strategy so you can ship changes safely."
                },
                new BulletListBlock { Items = items }
            }
        };
    }

    private static ReportSection BuildStep5_TechStackAndRuntimes(CompiledFlightPlan plan)
    {
        var platformById = plan.Entities.Platforms
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToDictionary(p => p.Id, StringComparer.Ordinal);

        var byPlatform = plan.Entities.Services
            .GroupBy(s => (s.PlatformRef ?? "(unspecified)").Trim(), StringComparer.Ordinal)
            .Select(g => new
            {
                PlatformId = g.Key,
                Count = g.Count(),
                PlatformName = platformById.TryGetValue(g.Key, out var p) ? p.Name : g.Key
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.PlatformName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var top = byPlatform.Take(5).ToList();

        string SummaryLine()
        {
            if (top.Count == 0) return "(not specified)";
            return string.Join(", ", top.Select(x => $"{x.PlatformName} ({x.Count})"));
        }

        var infraKinds = plan.Entities.Resources
            .Select(r => (r.ResourceKind ?? string.Empty).Trim())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Kind)
            .ToList();

        var items = new List<ListItem>
        {
            new() { Text = $"Technology: {SummaryLine()}." },
            new() { Text = $"Hosting Infrastructure={plan.DeliveryModel?.Hosting ?? "(not specified)"}." },
        };

        if (infraKinds.Count > 0)
        {
            items.Add(new ListItem
            {
                Text = $"Resource types: {string.Join(", ", infraKinds.Select(x => $"{x.Kind} ({x.Count})"))}."
            });
        }

        return new ReportSection
        {
            Heading = "Tech Stack & Runtimes",
            Level = 1,
            Anchor = "step-5-tech-stack",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text = "Get an aggregated sense of what you’ll be building and running."
                },
                new BulletListBlock { Items = items },
                new ParagraphBlock
                { 
                    Text = $"{Link(ServiceCatalogReportFile, "Service Catalog")}: per-service platform/repo details if you need specifics." 
                },
            }
        };
    }

    private static ReportSection BuildStep6_DataAndState(CompiledFlightPlan plan)
    {
        var dataGov = GetAnnotationMap(plan, "dataGovernance");

        var classifiedExportCount = plan.Interfaces.Count(i => i.DataClassRefs is { Count: > 0 });
        var classifiedResourceCount = plan.Entities.Resources.Count(r => r.DataClassRefs is { Count: > 0 });

        var primaryStore = TryGetAnnotationString(plan, "dataGovernance", "systemOfRecord")
                          ?? TryGetAnnotationString(plan, "dataGovernance", "system_of_record");

        var resourceKinds = plan.Entities.Resources
            .Select(r => (r.ResourceKind ?? string.Empty).Trim())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k)
            .ToList();

        var items = new List<ListItem>
        {
            new() { Text = $"{Link(ArchitectureReportFile + "#data-usage", "Architecture — Data Usage")}: where classified data is stored/transmitted." },
            new() { Text = $"{Link(SecurityReportFile + "#external-exposure", "Security — External Exposure")}: cross-check any externally exposed exports that carry classifications." },
            new() { Text = $"{Link(CompiledPlanFile, "Generated")}: classified exports={classifiedExportCount}, classified resources={classifiedResourceCount}." },
            new() { Text = $"{Link(InputsPlanFile, "Source")}: resources + dataClasses + annotations.dataGovernance.*." }
        };

        if (!string.IsNullOrWhiteSpace(primaryStore))
        {
            items.Add(new ListItem
            {
                Text = $"{Link(InputsPlanFile, "System of record")}: {primaryStore}."
            });
        }

        if (resourceKinds.Count > 0)
        {
            items.Add(new ListItem
            {
                Text = $"Resources: {string.Join(", ", resourceKinds)}."
            });
        }

        if (dataGov is { Count: > 0 })
            items.AddRange(FormatAnnotationMapItems(dataGov, maxItems: 6, sourcePrefix: "dataGovernance", sourceLink: InputsPlanFile));

        if (classifiedExportCount > 0 || classifiedResourceCount > 0)
        {
            items.Add(new ListItem
            {
                Text = $"{Link(SecurityReportFile, "PII/PHI warning")}: treat classified data handling as a first-week priority (access, logging, minimization)."
            });
        }

        return new ReportSection
        {
            Heading = "Data & State",
            Level = 1,
            Anchor = "step-6-data-state",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text = "This is high leverage for new engineers: understand the system of record, classifications, and data movement before making changes."
                },
                new BulletListBlock { Items = items }
            }
        };
    }

    private static ReportSection BuildStep7_ConfigSecretsEnvironments(CompiledFlightPlan plan)
    {
        var secrets = TryGetAnnotationString(plan, "security", "secretsManagement")
                      ?? TryGetAnnotationString(plan, "security", "secrets");

        var envFlow = DescribeEnvironmentFlow(plan);

        var items = new List<ListItem>
        {
            new() { Text = $"{Link(ArchitectureReportFile + "#environment-overview", "Environment Overview")}: environment intent + promotion paths." },
            new() { Text = $"{Link(InputsPlanFile, "Source")}: environments + annotations.security.* / annotations.deployment.*." },
            new() { Text = $"{Link(CompiledPlanFile, "Generated")}: environments modeled={plan.Entities.Environments.Count}." }
        };

        if (!string.IsNullOrWhiteSpace(envFlow))
            items.Add(new ListItem { Text = $"{Link(ArchitectureReportFile + "#environment-overview", "Environment scoping")}: {envFlow}." });

        if (!string.IsNullOrWhiteSpace(secrets))
        {
            items.Add(new ListItem
            {
                Text = $"{Link(InputsPlanFile, "Secrets")}: {secrets}."
            });
        }
        else
        {
            items.Add(new ListItem
            {
                Text = $"{Link(InputsPlanFile, "Source")}: consider adding annotations.security.secretsManagement (Vault, cloud secrets manager, etc)."
            });
        }

        return new ReportSection
        {
            Heading = "Config, Secrets & Environments",
            Level = 1,
            Anchor = "step-7-config-secrets",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text = "Learn where configuration is defined, where secrets live, and what changes are safe in each environment."
                },
                new BulletListBlock { Items = items }
            }
        };
    }

    private static ReportSection BuildStep8_ObservabilityAndDebugging(CompiledFlightPlan plan)
    {
        var obs = GetAnnotationMap(plan, "observability");
        var toolchainObs = GetToolingFromDefaultToolchain(plan, "observability");

        var items = new List<ListItem>
        {
            new() { Text = $"{Link(ArchitectureReportFile + "#observability-ops", "Architecture — Observability + Operations")}: expectations + operating model." },
            new() { Text = $"{Link(ArchitectureReportFile + "#tooling-overview", "Tooling overview")}: observability tools and URLs." },
            new() { Text = $"{Link(InputsPlanFile, "Source")}: annotations.observability.* (logging/metrics/tracing/runbooks)." }
        };

        if (!string.IsNullOrWhiteSpace(toolchainObs.linkText))
        {
            items.Add(new ListItem
            {
                Text = $"{Link(ArchitectureReportFile + "#toolchain-overview", "Default observability tooling")}: {toolchainObs.linkText}."
            });
        }

        if (obs is { Count: > 0 })
        {
            items.AddRange(FormatAnnotationMapItems(obs, maxItems: 8, sourcePrefix: "observability", sourceLink: InputsPlanFile));
        }
        else
        {
            items.Add(new ListItem
            {
                Text = $"{Link(InputsPlanFile, "Source")}: add annotations.observability (dashboards + runbooks) to make onboarding fast."
            });
        }

        return new ReportSection
        {
            Heading = "Observability & Debugging",
            Level = 1,
            Anchor = "step-8-observability",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text = "Know what to look at when something breaks: logs, dashboards, traces, and the runbook path."
                },
                new BulletListBlock { Items = items }
            }
        };
    }

    private static ReportSection BuildStep9_SecurityAndAccess(CompiledFlightPlan plan)
    {
        var security = GetAnnotationMap(plan, "security");
        var entryExports = GetEntryExports(plan);
        var terminalEntryExports = FilterToTerminalEntryExports(plan, entryExports);

        var entryAuth = terminalEntryExports
            .Select(e => (e.Auth ?? string.Empty).Trim())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a)
            .ToList();

        var externallyExposedCount = plan.Interfaces.Count(IsExternallyExposed);
        var publicZoneId = plan.Entities.Zones.FirstOrDefault(z => string.Equals(z.Trust, "public", StringComparison.OrdinalIgnoreCase) || string.Equals(z.Id, "public", StringComparison.OrdinalIgnoreCase))?.Id;
        var servicesInPublic = !string.IsNullOrWhiteSpace(publicZoneId)
            ? plan.Entities.Services.Count(s => string.Equals(s.ZoneRef, publicZoneId, StringComparison.OrdinalIgnoreCase))
            : plan.Entities.Services.Count(s => string.Equals(s.ZoneRef, "public", StringComparison.OrdinalIgnoreCase));

        var items = new List<ListItem>
        {
            new() { Text = $"{Link(SecurityReportFile, "Security Overview")}: full security analysis (exposure + trust boundaries)." },
            new() { Text = $"{Link(SecurityReportFile + "#external-exposure", "Interfaces & Exposure")}: externally exposed exports={externallyExposedCount}." },
            new() { Text = $"{Link(SecurityReportFile + "#trust-boundary-analysis", "Trust Boundaries")}: cross-zone calls and violations." },
            new() { Text = $"{Link(ServiceCatalogReportFile + "#entry-points", "Entry points")}: auth types observed: {(entryAuth.Count > 0 ? string.Join(", ", entryAuth) : "(not specified)")}." },
            new() { Text = $"{Link(CompiledPlanFile, "Generated")}: services in public zone (best-effort)={servicesInPublic}." },
            new() { Text = $"{Link(InputsPlanFile, "Source")}: annotations.security.* for authn/authz/audit/encryption details." }
        };

        if (security is { Count: > 0 })
            items.AddRange(FormatAnnotationMapItems(security, maxItems: 8, sourcePrefix: "security", sourceLink: InputsPlanFile));

        return new ReportSection
        {
            Heading = "Security & Access Basics",
            Level = 1,
            Anchor = "step-9-security",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text = "Get the basics right early: auth, authorization model, audit expectations, and what’s allowed to be public." 
                },
                new BulletListBlock { Items = items }
            }
        };
    }

    private static string EnsureLinked(string text, string fallbackHref, string fallbackLabel)
    {
        if (text.Contains("](", StringComparison.Ordinal)) return text;
        return $"{Link(fallbackHref, fallbackLabel)} — {text}";
    }

    private static Dictionary<string, object>? GetAnnotationMap(CompiledFlightPlan plan, params string[] path)
    {
        if (plan.Annotations is null || plan.Annotations.Count == 0) return null;

        object? current = plan.Annotations;
        foreach (var key in path)
        {
            if (current is Dictionary<string, object> dict)
            {
                if (!dict.TryGetValue(key, out current)) return null;
            }
            else
            {
                return null;
            }
        }

        return current as Dictionary<string, object>;
    }

    private static List<Dictionary<string, object>>? GetAnnotationMapList(CompiledFlightPlan plan, params string[] path)
    {
        if (plan.Annotations is null || plan.Annotations.Count == 0) return null;

        object? current = plan.Annotations;
        foreach (var key in path)
        {
            if (current is Dictionary<string, object> dict)
            {
                if (!dict.TryGetValue(key, out current)) return null;
            }
            else
            {
                return null;
            }
        }

        if (current is not System.Collections.IEnumerable seq || current is string) return null;

        var list = new List<Dictionary<string, object>>();
        foreach (var item in seq)
        {
            switch (item)
            {
                case Dictionary<string, object> dict:
                    list.Add(dict);
                    break;
                case IReadOnlyDictionary<string, object> ro:
                    list.Add(ro.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal));
                    break;
            }
        }

        return list.Count == 0 ? null : list;
    }

    private static string? TryGetAnnotationString(CompiledFlightPlan plan, params string[] path)
    {
        if (plan.Annotations is null || plan.Annotations.Count == 0) return null;

        object? current = plan.Annotations;
        foreach (var key in path)
        {
            if (current is Dictionary<string, object> dict)
            {
                if (!dict.TryGetValue(key, out current)) return null;
            }
            else
            {
                return null;
            }
        }

        return current switch
        {
            null => null,
            string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
            bool b => b.ToString(),
            int i => i.ToString(),
            long l => l.ToString(),
            double d => d.ToString(),
            decimal m => m.ToString(),
            _ => current.ToString()
        };
    }

    private static List<ListItem> FormatAnnotationMapItems(
        Dictionary<string, object> map,
        int maxItems,
        string sourcePrefix,
        string sourceLink)
    {
        if (map.Count == 0) return [];

        var items = new List<ListItem>();

        foreach (var (k, v) in map.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).Take(maxItems))
        {
            var key = (k ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;

            var text = FormatAnnotationValue(v, sourceLinkFallback: ArchitectureReportFile + "#tooling-overview");
            // Each list item is link-first via the source key link.
            var label = $"{sourcePrefix}.{key}";
            items.Add(new ListItem { Text = $"{Link(sourceLink, label)}: {text}" });
        }

        return items;
    }

    private static string FormatAnnotationValue(object? value, string sourceLinkFallback)
    {
        if (value is null) return "(not specified)";

        if (value is string s)
        {
            return LinkifyText(s.Trim(), sourceLinkFallback);
        }

        if (value is bool or int or long or double or decimal)
        {
            return value.ToString() ?? string.Empty;
        }

        if (value is List<object> list)
        {
            if (list.Count == 0) return "(none)";
            var parts = list
                .Select(x => LinkifyText(x?.ToString() ?? string.Empty, sourceLinkFallback))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(6)
                .ToList();

            return parts.Count > 0 ? string.Join(", ", parts) : "(none)";
        }

        if (value is Dictionary<string, object> nested)
        {
            // Render nested maps as a short inline summary.
            var parts = nested
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .Select(kvp => $"{kvp.Key}={LinkifyText(kvp.Value?.ToString() ?? string.Empty, sourceLinkFallback)}")
                .ToList();

            return parts.Count > 0 ? string.Join(", ", parts) : "(empty)";
        }

        return LinkifyText(value.ToString() ?? string.Empty, sourceLinkFallback);
    }

    private static string LinkifyText(string text, string fallbackHref)
    {
        var t = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(t)) return "(not specified)";

        // If the value already contains a Markdown link, keep it.
        if (t.Contains("](", StringComparison.Ordinal)) return t;

        // If it contains an http/https URL, linkify the URL portion.
        var httpIndex = t.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        if (httpIndex < 0) httpIndex = t.IndexOf("https://", StringComparison.OrdinalIgnoreCase);

        if (httpIndex >= 0)
        {
            var prefix = t[..httpIndex].TrimEnd();
            var url = t[httpIndex..].Trim();

            // URL ends at first whitespace.
            var split = url.IndexOfAny([' ', '\t', '\r', '\n']);
            if (split > 0) url = url[..split].Trim();

            if (Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                var link = $"[{url}]({url})";
                return string.IsNullOrWhiteSpace(prefix) ? link : $"{prefix} {link}";
            }
        }

        // Otherwise, keep as plain text; the list item itself is already link-first.
        return t;
    }

    private static string? TryGetHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        return string.IsNullOrWhiteSpace(uri.Host) ? null : uri.Host.Trim();
    }

    private static (string toolingId, string linkText) GetToolingFromDefaultToolchain(CompiledFlightPlan plan, string key)
    {
        if (plan.Toolchain is null || plan.Toolchain.Count == 0) return (string.Empty, string.Empty);
        if (!plan.Toolchain.TryGetValue(key, out var toolingId)) return (string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(toolingId)) return (string.Empty, string.Empty);

        var tool = plan.Entities.Tooling.FirstOrDefault(t => string.Equals(t.Id, toolingId, StringComparison.Ordinal));
        if (tool is null)
        {
            return (toolingId, Link(ArchitectureReportFile + "#tooling-overview", toolingId));
        }

        if (!string.IsNullOrWhiteSpace(tool.Url))
        {
            return (toolingId, $"[{tool.Name}]({tool.Url})");
        }

        return (toolingId, Link(ArchitectureReportFile + "#tooling-overview", tool.Name));
    }

    private static string DescribeEnvironmentFlow(CompiledFlightPlan plan)
    {
        var envs = plan.Entities.Environments;
        if (envs.Count == 0) return string.Empty;

        var promotesTo = envs
            .Where(e => e.PromotesTo is { Count: > 0 })
            .ToDictionary(e => e.Id, e => e.PromotesTo!.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);

        if (promotesTo.Count == 0) return "(no promotion paths declared)";

        var incoming = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, tos) in promotesTo)
        {
            foreach (var to in tos)
            {
                incoming.TryGetValue(to, out var count);
                incoming[to] = count + 1;
            }
        }

        var roots = promotesTo.Keys.Where(k => !incoming.ContainsKey(k)).OrderBy(k => k).ToList();
        if (roots.Count == 0) roots = promotesTo.Keys.OrderBy(k => k).Take(1).ToList();

        // Render a single best-effort chain from the first root.
        var start = roots[0];
        var chain = new List<string> { start };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var current = start;

        while (promotesTo.TryGetValue(current, out var nexts) && nexts.Count > 0)
        {
            var next = nexts.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).First();
            if (!seen.Add(next)) break;
            chain.Add(next);
            current = next;
            if (chain.Count >= 8) break;
        }

        return string.Join(" → ", chain);
    }

    private static List<ListItem> BuildToolchainListItems(CompiledFlightPlan plan)
    {
        if (plan.Toolchain == null || plan.Toolchain.Count == 0) return [];

        var toolingById = plan.Entities.Tooling
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToDictionary(t => t.Id, StringComparer.Ordinal);

        string ToolLink(string toolingId)
        {
            if (toolingById.TryGetValue(toolingId, out var tool))
            {
                if (!string.IsNullOrWhiteSpace(tool.Url))
                {
                    // Prefer direct tool URL link when present.
                    return $"[{tool.Name}]({tool.Url})";
                }
            }

            // Fall back to the tooling overview section.
            return Link(ArchitectureReportFile + "#tooling-overview", toolingId);
        }

        return plan.Toolchain
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => new ListItem
            {
                Text = $"{Link(ArchitectureReportFile + "#toolchain-overview", kvp.Key)}: {ToolLink(kvp.Value)}"
            })
            .ToList();
    }

    private static string Link(string href, string text)
    {
        return $"[{text}]({href})";
    }

    private static bool IsExternallyExposed(ServiceExport export)
    {
        if (export == null) return false;

        if (export.Consumers is { Count: > 0 }) return true;

        return string.Equals(export.Visibility, "external", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(export.Visibility, "public", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatRepoLinkCell(string? repoUrl)
    {
        var trimmed = (repoUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return string.Empty;

        // Render as Markdown link so HTML renderer can decorate with a host icon.
        var name = TryGetRepoDisplayName(trimmed) ?? "repo";
        return $"[{name}]({trimmed})";
    }

    private static string? TryGetRepoDisplayName(string repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl)) return null;
        if (!Uri.TryCreate(repoUrl.Trim(), UriKind.Absolute, out var uri)) return null;

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var host = (uri.Host ?? string.Empty).Trim().ToLowerInvariant();
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Uri.UnescapeDataString(s))
            .ToArray();

        if (segments.Length == 0) return null;

        // Common patterns
        // - GitHub/GitLab/Bitbucket: /{owner}/{repo}
        // Requirement: show the repo name parsed from the end of the URL.
        if (host is "github.com" or "gitlab.com" or "bitbucket.org")
        {
            return StripGitSuffix(segments[^1]);
        }

        // Azure DevOps: /{org}/{project}/_git/{repo} or /{project}/_git/{repo}
        if (host is "dev.azure.com" || host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            var gitIndex = Array.FindIndex(segments, s => string.Equals(s, "_git", StringComparison.OrdinalIgnoreCase));
            if (gitIndex >= 0 && gitIndex + 1 < segments.Length)
            {
                var repo = StripGitSuffix(segments[gitIndex + 1]);
                return string.IsNullOrWhiteSpace(repo) ? null : repo;
            }
        }

        // Default: last segment
        return StripGitSuffix(segments[^1]);
    }

    private static string StripGitSuffix(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        return text;
    }

    private static ReportSection BuildWorkingWithFlightPlan(CompiledFlightPlan plan)
    {
        return new ReportSection
        {
            Heading = "Working with Flight Plan (this repo)",
            Level = 1,
            Anchor = "working-with-flightplan",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Use these commands to validate the model and regenerate the published docs. If you’re looking at published output, start at the table of contents." 
                },
                new BulletListBlock
                {
                    Items =
                    {
                        new ListItem { Text = $"Open {Link(TocFile, "published reports")}." },
                        new ListItem { Text = $"Browse {Link("flightplan.compiled.json", "compiled plan JSON")} for machine-readable structure." }
                    }
                },
                new CodeBlock
                {
                    Language = "bash",
                    Code =
                        "# Validate the YAML model\n" +
                        "flightplan verify ./flightplan.yaml\n\n" +
                        "# Generate reports (HTML)\n" +
                        "flightplan publish ./flightplan.yaml --format html -o ./publish --overwrite"
                },
                new ParagraphBlock
                {
                    Text =
                        "Source-of-truth inputs are the YAML files (including any referenced via uses). The publish output includes a compiled JSON representation plus generated reports." 
                }
            }
        };
    }

    private static List<ServiceExport> GetEntryExports(CompiledFlightPlan plan)
    {
        return plan.Interfaces
            .Where(i =>
                string.Equals(i.Visibility, "external", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.Visibility, "public", StringComparison.OrdinalIgnoreCase) ||
                (i.Consumers is { Count: > 0 }))
            .GroupBy(i => i.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private static List<ServiceExport> FilterToTerminalEntryExports(CompiledFlightPlan plan, List<ServiceExport> entryExports)
    {
        if (entryExports.Count <= 1) return entryExports;

        // Entry services are the services that host entry exports.
        var entryServiceIds = entryExports
            .Select(e => e.ServiceRef)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (entryServiceIds.Count <= 1) return entryExports;

        // Build adjacency across services (service-to-service dependencies).
        var outbound = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var dep in plan.Dependencies)
        {
            if (!string.Equals(dep.Kind, "service-to-service", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(dep.From) || string.IsNullOrWhiteSpace(dep.To)) continue;

            // dep.To is exportId (service/export). We care about the service portion.
            var toService = dep.To;
            var slash = dep.To.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0) toService = dep.To[..slash];

            if (!outbound.TryGetValue(dep.From, out var list))
            {
                list = [];
                outbound[dep.From] = list;
            }

            list.Add(toService);
        }

        var reachableByEntry = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var start in entryServiceIds)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(start);
            visited.Add(start);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!outbound.TryGetValue(current, out var next)) continue;

                foreach (var n in next)
                {
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    if (visited.Add(n)) queue.Enqueue(n);
                }
            }

            // Remove itself for convenience.
            visited.Remove(start);
            reachableByEntry[start] = visited;
        }

        var nonTerminal = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in entryServiceIds)
        {
            foreach (var b in entryServiceIds)
            {
                if (a == b) continue;
                if (reachableByEntry.TryGetValue(a, out var reach) && reach.Contains(b))
                {
                    nonTerminal.Add(b);
                }
            }
        }

        var terminalEntryServiceIds = entryServiceIds.Where(s => !nonTerminal.Contains(s)).ToHashSet(StringComparer.Ordinal);
        if (terminalEntryServiceIds.Count == 0) return entryExports;

        // Always keep exports with explicit consumers.
        var filtered = entryExports
            .Where(e => terminalEntryServiceIds.Contains(e.ServiceRef) || (e.Consumers is { Count: > 0 }))
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        return filtered.Count == 0 ? entryExports : filtered;
    }
}
