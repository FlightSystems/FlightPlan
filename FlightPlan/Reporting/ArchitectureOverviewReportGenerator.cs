namespace FlightPlan.Reporting;

/// <summary>
/// Produces a high-level Architecture Overview report
/// from a validated CompiledFlightPlan.
/// </summary>
public sealed class ArchitectureOverviewReportGenerator : IReportGenerator
{
    private ReportGenerationOptions _options = new();
    
    public ReportDocument Generate(CompiledFlightPlan plan)
    {
        return Generate(plan, new ReportGenerationOptions());
    }
    
    public ReportDocument Generate(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        _options = options ?? new ReportGenerationOptions();

        var document = new ReportDocument
        {
            Title = "Architecture Overview",
            Subtitle = plan.Application?.Description,
            Metadata = new ReportMetadata
            {
                ApplicationName = plan.Application?.Name,
                Version = plan.Metadata.Format,
                Tags =
                {
                    ["report"] = "architecture-overview",
                    ["generated-by"] = "flightplan"
                }
            }
        };

        // Narrative-first ordering: orient the reader, show context/views/functionality,
        // then data and operational readiness, and finally inventories.
        document.Sections.Add(BuildIntendedUse());
        document.Sections.Add(BuildExecutiveSummary(plan));
        document.Sections.Add(BuildSystemContext(plan));
        document.Sections.Add(BuildArchitectureViews(plan));
        document.Sections.Add(BuildSystemFunctionality(plan));
        document.Sections.Add(BuildDataClassOverview(plan));
        document.Sections.Add(BuildDataUsageOverview(plan));
        document.Sections.Add(BuildOwnershipAndSupport(plan));
        document.Sections.Add(BuildOperationalReadiness(plan));
        document.Sections.Add(BuildEnvironmentOverview(plan));
        document.Sections.Add(BuildPlatformOverview(plan));
        
        var toolchain = BuildToolchainOverview(plan);
        if (toolchain != null) document.Sections.Add(toolchain);
        
        var tooling = BuildToolingOverview(plan);
        if (tooling != null) document.Sections.Add(tooling);
        
        document.Sections.Add(BuildServiceOverview(plan));
        document.Sections.Add(BuildResourceOverview(plan));
        
        document.Findings.AddRange(CollectFindings(plan));

        return document;
    }

    private static List<Finding> CollectFindings(CompiledFlightPlan plan)
    {
        var findings = new List<Finding>();

        if (plan.Entities.Environments.Count == 0)
        {
            findings.Add(new Finding
            {
                ReportId = "architecture-overview",
                SectionHeading = "Executive Summary",
                SectionAnchorId = "executive-summary",
                Severity = FindingSeverity.Medium,
                Title = "No environments defined",
                Summary = "The flight plan defines no environments; environment-specific architecture and deployments cannot be described.",
            });
        }

        if (plan.Entities.Resources.Count == 0)
        {
            findings.Add(new Finding
            {
                ReportId = "architecture-overview",
                SectionHeading = "Resource Overview",
                SectionAnchorId = "resource-overview",
                Severity = FindingSeverity.Medium,
                Title = "No resources modeled",
                Summary = "No supporting resources are defined. If the system relies on shared infrastructure (databases, queues, caches, external APIs), model them as resources for better traceability.",
            });
        }

        var servicesMissingOwner = plan.Entities.Services
            .Where(s => string.IsNullOrWhiteSpace(s.OwnerRef))
            .Select(s => s.Name)
            .OrderBy(n => n)
            .ToList();

        if (servicesMissingOwner.Count > 0)
        {
            findings.Add(new Finding
            {
                ReportId = "architecture-overview",
                SectionHeading = "Service Overview",
                SectionAnchorId = "service-overview",
                Severity = FindingSeverity.Medium,
                Title = "Services missing owner assignment",
                Summary = $"{servicesMissingOwner.Count} service(s) do not declare an owner/team. This makes operational responsibility unclear.",
                Evidence = servicesMissingOwner.Take(10).ToList()
            });
        }

        var servicesMissingZone = plan.Entities.Services
            .Where(s => string.IsNullOrWhiteSpace(s.ZoneRef))
            .Select(s => s.Name)
            .OrderBy(n => n)
            .ToList();

        if (servicesMissingZone.Count > 0)
        {
            findings.Add(new Finding
            {
                ReportId = "architecture-overview",
                SectionHeading = "Service Overview",
                SectionAnchorId = "service-overview",
                Severity = FindingSeverity.Medium,
                Title = "Services missing zone assignment",
                Summary = $"{servicesMissingZone.Count} service(s) do not declare a zone. Zoning is important for trust boundaries, access policies, and dependency reviews.",
                Evidence = servicesMissingZone.Take(10).ToList()
            });
        }

        var servicesMissingRepo = plan.Entities.Services
            .Where(s => string.IsNullOrWhiteSpace(s.RepoUrl))
            .Select(s => s.Name)
            .OrderBy(n => n)
            .ToList();

        if (servicesMissingRepo.Count > 0)
        {
            findings.Add(new Finding
            {
                ReportId = "architecture-overview",
                SectionHeading = "Service Overview",
                SectionAnchorId = "service-overview",
                Severity = FindingSeverity.Low,
                Title = "Services missing repo URL",
                Summary = $"{servicesMissingRepo.Count} service(s) do not declare a repoUrl. Adding repo links improves traceability for ownership, releases, and incident response.",
                Evidence = servicesMissingRepo.Take(10).ToList()
            });
        }

        var dataClassUsageCount = plan.Interfaces.Count(i => i.DataClassRefs is { Count: > 0 }) +
                                 plan.Entities.Resources.Count(r => r.DataClassRefs is { Count: > 0 });

        if (plan.Entities.DataClasses.Count == 0 && dataClassUsageCount == 0 && (plan.Entities.Services.Count > 0 || plan.Entities.Resources.Count > 0))
        {
            findings.Add(new Finding
            {
                ReportId = "architecture-overview",
                SectionHeading = "Data Usage",
                SectionAnchorId = "data-usage",
                Severity = FindingSeverity.Low,
                Title = "No data classifications used",
                Summary = "No data classifications are modeled or referenced by exports/resources. Adding even a small set of classifications (e.g., PII/PHI/PCI/Internal) improves security and compliance discussions.",
            });
        }

        var servicesMissingPlatform = plan.Entities.Services
            .Where(s => string.IsNullOrWhiteSpace(s.PlatformRef))
            .Select(s => s.Name)
            .OrderBy(n => n)
            .ToList();

        if (servicesMissingPlatform.Count > 0)
        {
            findings.Add(new Finding
            {
                ReportId = "architecture-overview",
                SectionHeading = "Service Overview",
                SectionAnchorId = "service-overview",
                Severity = FindingSeverity.Medium,
                Title = "Services missing platform assignment",
                Summary = $"{servicesMissingPlatform.Count} service(s) do not declare a platform. This reduces clarity on runtime foundations and operational responsibilities.",
                Evidence = servicesMissingPlatform.Take(10).ToList()
            });
        }

        return findings;
    }

    private static ReportSection BuildArchitectureViews(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "Architecture Views",
            Level = 1,
            Anchor = "architecture-views",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section links to the key architecture views (system boundary, containers, deployments, and trust boundaries). " +
                        "Model these under annotations.architectureViews so teams can keep diagrams and narratives discoverable from the report."
                }
            }
        };

        // New canonical shape:
        // annotations.architectureViews: [{ name, description, url }]
        if (TryGetAnnotationMapList(plan.Annotations, "architectureViews", out var diagramList) && diagramList.Count > 0)
        {
            var table = new TableBlock
            {
                Headers = { "Name", "Description" }
            };

            foreach (var diagram in diagramList)
            {
                TryGetString(diagram, "name", out var name);
                TryGetString(diagram, "description", out var description);
                TryGetString(diagram, "url", out var url);

                var linkedName = string.IsNullOrWhiteSpace(url)
                    ? (name ?? string.Empty)
                    : $"[{(name ?? "link")}]({url})";

                table.Rows.Add(new TableRow
                {
                    Cells =
                    {
                        linkedName,
                        description ?? string.Empty,
                    }
                });
            }

            section.Blocks.Add(new ParagraphBlock { Text = "Diagrams:" });
            section.Blocks.Add(table);
            return section;
        }

        if (!TryGetAnnotationMap(plan.Annotations, "architectureViews", out var views))
        {
            section.Blocks.Add(NotModeledCallout("Architecture views"));
            return section;
        }

        var kv = new KeyValueTableBlock();
        foreach (var key in new[]
                 {
                     "contextDiagram",
                     "containerDiagram",
                     "deploymentDiagram",
                     "dataFlowDiagram",
                     "trustBoundaries",
                     "threatModel",
                     "sequenceDiagrams",
                     "notes"
                 })
        {
            if (TryGetString(views, key, out var value) && !string.IsNullOrWhiteSpace(value))
                kv.Rows.Add(kv.New(key, value));
        }

        if (kv.Rows.Count > 0)
            section.Blocks.Add(kv);

        var diagrams = new List<Dictionary<string, object>>();
        var links = new List<string>();

        // Legacy optional structured diagram list:
        // annotations.architectureViews.diagrams: [{ name, type, url, status }]
        if (TryGetMapList(views, "diagrams", out diagrams) && diagrams.Count > 0)
        {
            var table = new TableBlock
            {
                Headers = { "Name", "Type", "Status" }
            };

            foreach (var diagram in diagrams)
            {
                TryGetString(diagram, "name", out var name);
                TryGetString(diagram, "type", out var type);
                TryGetString(diagram, "url", out var url);
                TryGetString(diagram, "status", out var status);

                var linkedName = string.IsNullOrWhiteSpace(url)
                    ? (name ?? string.Empty)
                    : $"[{(name ?? "link")}]({url})";

                table.Rows.Add(new TableRow
                {
                    Cells =
                    {
                        linkedName,
                        type ?? string.Empty,
                        status ?? string.Empty
                    }
                });
            }

            section.Blocks.Add(new ParagraphBlock { Text = "Diagrams:" });
            section.Blocks.Add(table);
        }

        TryGetStringList(views, "links", out links);
        if (links.Count > 0)
        {
            section.Blocks.Add(new ParagraphBlock { Text = "Links:" });
            section.Blocks.Add(new BulletListBlock { Items = links.Select(l => new ListItem { Text = l }).ToList() });
        }

        if (kv.Rows.Count == 0 && diagrams.Count == 0 && links.Count == 0)
        {
            section.Blocks.Add(NotModeledCallout("Architecture views (values)"));
        }

        return section;
    }

    private static ReportSection BuildOwnershipAndSupport(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "Ownership + Support",
            Level = 1,
            Anchor = "ownership-support",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section clarifies who owns and operates the system (and how to get help). " +
                        "Model system-level details under annotations.ownership, and optionally service-level details under service.annotations.ownership."
                }
            }
        };

        if (TryGetAnnotationMap(plan.Annotations, "ownership", out var ownership))
        {
            var kv = new KeyValueTableBlock();
            foreach (var key in new[] { "primaryTeam", "technicalOwner", "productOwner", "onCall", "supportModel", "escalation", "communicationChannels" })
            {
                if (TryGetString(ownership, key, out var value) && !string.IsNullOrWhiteSpace(value))
                    kv.Rows.Add(kv.New(key, value));
            }

            if (kv.Rows.Count > 0)
                section.Blocks.Add(kv);

            if (TryGetStringList(ownership, "contacts", out var contacts) && contacts.Count > 0)
            {
                section.Blocks.Add(new ParagraphBlock { Text = "Contacts:" });
                section.Blocks.Add(new BulletListBlock { Items = contacts.Select(c => new ListItem { Text = c }).ToList() });
            }
        }
        else
        {
            section.Blocks.Add(NotModeledCallout("System ownership/support"));
        }

        // Avoid duplicating the full Service Overview table here.
        // Instead, provide a coverage rollup and list only the gaps.
        var services = plan.Entities.Services.OrderBy(s => s.Name).ToList();
        section.Blocks.Add(new ParagraphBlock { Text = "See full inventory in [Service Overview](#service-overview)." });

        string ResolveOwnerDisplay(ServiceEntity svc)
        {
            var ownerDisplay = (svc.OwnerRef ?? string.Empty).Trim();
            if (svc.Annotations != null && TryGetAnnotationMap(svc.Annotations, "ownership", out var svcOwnership))
            {
                if (TryGetString(svcOwnership, "team", out var team) && !string.IsNullOrWhiteSpace(team))
                    ownerDisplay = team;
                if (TryGetString(svcOwnership, "owner", out var owner) && !string.IsNullOrWhiteSpace(owner))
                    ownerDisplay = ownerDisplay.Length == 0 ? owner : $"{ownerDisplay} ({owner})";
            }

            return ownerDisplay;
        }

        bool TryGetExplicitOnCall(ServiceEntity svc, out string onCall)
        {
            onCall = string.Empty;

            if (svc.Annotations == null)
                return false;

            if (TryGetAnnotationMap(svc.Annotations, "ownership", out var svcOwnership))
            {
                if (TryGetString(svcOwnership, "onCall", out var ownershipOnCall) && !string.IsNullOrWhiteSpace(ownershipOnCall))
                {
                    onCall = ownershipOnCall;
                    return true;
                }
            }

            if (TryGetString(svc.Annotations, "onCall", out var directOnCall) && !string.IsNullOrWhiteSpace(directOnCall))
            {
                onCall = directOnCall;
                return true;
            }

            return false;
        }

        bool TryGetTeamOnCall(ServiceEntity svc, out string onCall)
        {
            onCall = string.Empty;

            var teamId = svc.OwnerRef;
            if (string.IsNullOrWhiteSpace(teamId))
                return false;

            var team = plan.Entities.Teams.FirstOrDefault(t => string.Equals(t.Id, teamId, StringComparison.Ordinal));
            if (team?.Annotations == null)
                return false;

            if (TryGetAnnotationMap(team.Annotations, "ownership", out var ownership))
            {
                if (TryGetString(ownership, "onCall", out var teamOnCall) && !string.IsNullOrWhiteSpace(teamOnCall))
                {
                    onCall = teamOnCall;
                    return true;
                }
            }

            if (TryGetString(team.Annotations, "onCall", out var directOnCall) && !string.IsNullOrWhiteSpace(directOnCall))
            {
                onCall = directOnCall;
                return true;
            }

            return false;
        }

        string ResolveOnCallDisplay(ServiceEntity svc)
        {
            if (TryGetExplicitOnCall(svc, out var explicitOnCall))
                return explicitOnCall;

            if (TryGetTeamOnCall(svc, out var teamOnCall))
                return teamOnCall;

            // If there is an owner/team but no explicit on-call, assume ownership implies support.
            // This avoids forcing every service to specify an on-call rotation, while still encouraging explicit routing.
            var ownerDisplay = ResolveOwnerDisplay(svc);
            return string.IsNullOrWhiteSpace(ownerDisplay) ? string.Empty : $"{ownerDisplay} (assumed)";
        }

        var total = services.Count;
        var ownedCount = services.Count(s => !string.IsNullOrWhiteSpace(ResolveOwnerDisplay(s)));
        var explicitOnCallCount = services.Count(s => TryGetExplicitOnCall(s, out _));
        var inheritedTeamOnCallCount = services.Count(s => !TryGetExplicitOnCall(s, out _) && TryGetTeamOnCall(s, out _));
        var assumedOnCallCount = services.Count(s => !TryGetExplicitOnCall(s, out _) && !TryGetTeamOnCall(s, out _) && !string.IsNullOrWhiteSpace(ResolveOwnerDisplay(s)));
        var supportRouteCount = services.Count(s => !string.IsNullOrWhiteSpace(ResolveOnCallDisplay(s)));
        var repoCount = services.Count(s => !string.IsNullOrWhiteSpace(s.RepoUrl));

        section.Blocks.Add(new KeyValueTableBlock
        {
            Rows =
            {
                new KeyValueTableBlock().New("Services", total.ToString()),
                new KeyValueTableBlock().New("With Owner", $"{ownedCount}/{total}"),
                new KeyValueTableBlock().New("With Explicit On-Call", $"{explicitOnCallCount}/{total}"),
                new KeyValueTableBlock().New("Inherited from Team", $"{inheritedTeamOnCallCount}/{total}"),
                new KeyValueTableBlock().New("Assumed via Owner", $"{assumedOnCallCount}/{total}"),
                new KeyValueTableBlock().New("With Support Route", $"{supportRouteCount}/{total}"),
                new KeyValueTableBlock().New("With Repo URL", $"{repoCount}/{total}")
            }
        });

        // Roll up by owner/team to show responsibility distribution.
        var byOwner = services
            .Select(s => new
            {
                Service = s,
                Owner = ResolveOwnerDisplay(s),
                OnCall = ResolveOnCallDisplay(s)
            })
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Owner) ? "(unowned)" : x.Owner)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToList();

        var ownerTable = new TableBlock
        {
            Headers = { "Owner/Team", "Services (#)", "On-Call Coverage", "Examples" }
        };

        foreach (var group in byOwner)
        {
            var groupTotal = group.Count();
            var groupOnCall = group.Count(x => !string.IsNullOrWhiteSpace(x.OnCall));
            var examples = string.Join(", ", group.Select(x => CatalogLinkHelper.ServiceLink(x.Service)).OrderBy(n => n).Take(5));

            ownerTable.Rows.Add(new TableRow
            {
                Cells =
                {
                    group.Key,
                    groupTotal.ToString(),
                    $"{groupOnCall}/{groupTotal}",
                    examples
                }
            });
        }

        section.Blocks.Add(new ParagraphBlock { Text = "Ownership distribution:" });
        section.Blocks.Add(ownerTable);

        // Optional: highlight where on-call is not explicitly modeled (but will be assumed via owner).
        var missingExplicitOnCall = services
            .Select(s => new
            {
                Service = s,
                Owner = ResolveOwnerDisplay(s)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Owner) && !TryGetExplicitOnCall(x.Service, out _))
            .Where(x => !string.IsNullOrWhiteSpace(x.Owner) && !TryGetExplicitOnCall(x.Service, out _) && !TryGetTeamOnCall(x.Service, out _))
            .OrderBy(x => x.Service.Name)
            .Take(15)
            .Select(x => CatalogLinkHelper.ServiceLink(x.Service))
            .ToList();

        if (missingExplicitOnCall.Count > 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Info,
                Title = "On-call not explicitly modeled",
                Message = "Some services do not specify an on-call route; support will be assumed via the owner/team. " +
                          "Consider adding service.annotations.ownership.onCall for higher operational clarity (PagerDuty rota, Slack channel, etc.). " +
                          $"Examples: {string.Join(", ", missingExplicitOnCall)}"
            });
        }

        return section;
    }

    private static ReportSection BuildSystemFunctionality(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "System Functionality",
            Level = 1,
            Anchor = "system-functionality",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section summarizes what the system does, and maps that functionality to concrete services. " +
                        "Capabilities can be modeled per service via service.annotations.capabilities (or service.annotations.functions)."
                }
            }
        };

        section.Children.Add(BuildUserInterfacesFunctionality(plan));
        section.Children.Add(BuildProcessingFunctionality(plan));

        return section;
    }

    private ReportSection BuildOperationalReadiness(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "Operational Readiness",
            Level = 1,
            Anchor = "operational-readiness",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section captures best-practice operational and security expectations (SLOs, DR, observability, deployment, and governance). " +
                        "If details are not modeled yet, the report will call that out explicitly."
                }
            }
        };

        var nfr = BuildNonFunctionalTargets(plan, _options);
        if (nfr != null) section.Children.Add(nfr);
        
        var rel = BuildReliabilityAndDr(plan, _options);
        if (rel != null) section.Children.Add(rel);
        
        var obs = BuildObservabilityAndOps(plan, _options);
        if (obs != null) section.Children.Add(obs);
        
        var sec = BuildSecurityPosture(plan, _options);
        if (sec != null) section.Children.Add(sec);
        
        var dg = BuildDataGovernance(plan, _options);
        if (dg != null) section.Children.Add(dg);
        
        var dep = BuildDeploymentAndDelivery(plan, _options);
        if (dep != null) section.Children.Add(dep);
        
        var api = BuildApiGovernance(plan, _options);
        if (api != null) section.Children.Add(api);
        
        var ten = BuildTenancyModel(plan, _options);
        if (ten != null) section.Children.Add(ten);

        return section;
    }

    private static ReportSection? BuildNonFunctionalTargets(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        if (!TryGetAnnotationMap(plan.Annotations, "nonFunctional", out var nfr))
        {
            if (!options.ShowEmptySections)
                return null;
        }

        var section = new ReportSection
        {
            Heading = "Non-Functional Targets",
            Level = 2,
            Anchor = "nfr",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Target outcomes for reliability and performance: availability, latency, throughput, and error budget. " +
                        "Model these under annotations.nonFunctional (e.g., availability, latencyP95, throughputRps, errorBudgetPolicy)."
                }
            }
        };

        if (nfr == null)
        {
            section.Blocks.Add(NotModeledCallout("Non-functional targets"));
            return section;
        }

        var kv = new KeyValueTableBlock();
        foreach (var key in new[] { "availability", "latencyP95", "latencyP99", "throughputRps", "errorBudgetPolicy" })
        {
            if (TryGetString(nfr, key, out var value) && !string.IsNullOrWhiteSpace(value))
                kv.Rows.Add(kv.New(key, value));
        }

        if (kv.Rows.Count == 0)
        {
            if (!options.ShowEmptySections)
                return null;
            section.Blocks.Add(NotModeledCallout("Non-functional targets (values)"));
            return section;
        }

        section.Blocks.Add(kv);
        return section;
    }

    private static ReportSection? BuildReliabilityAndDr(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        if (!TryGetAnnotationMap(plan.Annotations, "reliability", out var rel))
        {
            if (!options.ShowEmptySections)
                return null;
        }

        var section = new ReportSection
        {
            Heading = "Reliability + DR",
            Level = 2,
            Anchor = "reliability-dr",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Disaster recovery and resiliency expectations: RTO/RPO, backups, restore testing, and failure-mode handling. " +
                        "Model under annotations.reliability (e.g., rto, rpo, backupStrategy, restoreTestCadence, idempotency, retryPolicy)."
                }
            }
        };

        if (rel == null)
        {
            section.Blocks.Add(NotModeledCallout("Reliability/DR"));
            return section;
        }

        var kv = new KeyValueTableBlock();
        foreach (var key in new[] { "rto", "rpo", "backupStrategy", "restoreTestCadence", "retryPolicy", "timeoutPolicy", "idempotency" })
        {
            if (TryGetString(rel, key, out var value) && !string.IsNullOrWhiteSpace(value))
                kv.Rows.Add(kv.New(key, value));
        }

        if (kv.Rows.Count == 0)
        {
            if (!options.ShowEmptySections)
                return null;
            section.Blocks.Add(NotModeledCallout("Reliability/DR (values)"));
            return section;
        }

        section.Blocks.Add(kv);
        return section;
    }

    private static ReportSection? BuildObservabilityAndOps(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        if (!TryGetAnnotationMap(plan.Annotations, "observability", out var obs))
        {
            if (!options.ShowEmptySections)
                return null;
        }

        var section = new ReportSection
        {
            Heading = "Observability + Operations",
            Level = 2,
            Anchor = "observability-ops",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "What you can see and how you operate it: logs, metrics, traces, dashboards, alerts, and runbooks. " +
                        "Model under annotations.observability (e.g., logging, metrics, tracing, dashboards, alerts, runbooks)."
                }
            }
        };

        if (obs == null)
        {
            section.Blocks.Add(NotModeledCallout("Observability/Operations"));
            return section;
        }

        var kv = new KeyValueTableBlock();
        foreach (var key in new[] { "logging", "metrics", "tracing", "dashboards", "alerts", "runbooks", "onCall" })
        {
            if (TryGetString(obs, key, out var value) && !string.IsNullOrWhiteSpace(value))
                kv.Rows.Add(kv.New(key, value));
        }

        if (kv.Rows.Count > 0)
            section.Blocks.Add(kv);

        if (TryGetStringList(obs, "dashboards", out var dashboards) && dashboards.Count > 0)
        {
            section.Blocks.Add(new ParagraphBlock { Text = "Dashboards:" });
            section.Blocks.Add(new BulletListBlock { Items = dashboards.Select(d => new ListItem { Text = d }).ToList() });
        }

        if (TryGetStringList(obs, "runbooks", out var runbooks) && runbooks.Count > 0)
        {
            section.Blocks.Add(new ParagraphBlock { Text = "Runbooks:" });
            section.Blocks.Add(new BulletListBlock { Items = runbooks.Select(r => new ListItem { Text = r }).ToList() });
        }

        if (kv.Rows.Count == 0 && (!TryGetStringList(obs, "dashboards", out dashboards) || dashboards.Count == 0) &&
            (!TryGetStringList(obs, "runbooks", out runbooks) || runbooks.Count == 0))
        {
            if (!options.ShowEmptySections)
                return null;
            section.Blocks.Add(NotModeledCallout("Observability/Operations (values)"));
        }

        return section;
    }

    private static ReportSection? BuildSecurityPosture(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        if (!TryGetAnnotationMap(plan.Annotations, "security", out var sec))
        {
            if (!options.ShowEmptySections)
                return null;
        }

        var section = new ReportSection
        {
            Heading = "Security Posture",
            Level = 2,
            Anchor = "security-posture",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "High-level security architecture: authn/authz model, boundary controls, encryption, secrets, and audit expectations. " +
                        "Model under annotations.security (e.g., authModel, authorizationModel, auditLogging, encryptionAtRest, encryptionInTransit, secretsManagement)."
                }
            }
        };

        if (sec == null)
        {
            section.Blocks.Add(NotModeledCallout("Security posture"));
            return section;
        }

        var kv = new KeyValueTableBlock();
        foreach (var key in new[] { "authModel", "authorizationModel", "auditLogging", "encryptionAtRest", "encryptionInTransit", "secretsManagement", "keyManagement" })
        {
            if (TryGetString(sec, key, out var value) && !string.IsNullOrWhiteSpace(value))
                kv.Rows.Add(kv.New(key, value));
        }

        if (kv.Rows.Count == 0)
        {
            if (!options.ShowEmptySections)
                return null;
            section.Blocks.Add(NotModeledCallout("Security posture (values)"));
            return section;
        }

        section.Blocks.Add(kv);
        return section;
    }

    private static ReportSection? BuildDataGovernance(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        if (!TryGetAnnotationMap(plan.Annotations, "dataGovernance", out var dg))
        {
            if (!options.ShowEmptySections)
                return null;
        }

        var section = new ReportSection
        {
            Heading = "Data Governance",
            Level = 2,
            Anchor = "data-governance",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Data ownership and lifecycle: system-of-record, retention/deletion, access controls, and compliance requirements. " +
                        "Model under annotations.dataGovernance (e.g., systemOfRecord, retentionPolicy, deletionPolicy, compliance)."
                }
            }
        };

        if (dg == null)
        {
            section.Blocks.Add(NotModeledCallout("Data governance"));
            return section;
        }

        var kv = new KeyValueTableBlock();
        foreach (var key in new[] { "systemOfRecord", "retentionPolicy", "deletionPolicy", "accessModel", "compliance" })
        {
            if (TryGetString(dg, key, out var value) && !string.IsNullOrWhiteSpace(value))
                kv.Rows.Add(kv.New(key, value));
        }

        if (kv.Rows.Count == 0)
        {
            if (!options.ShowEmptySections)
                return null;
            section.Blocks.Add(NotModeledCallout("Data governance (values)"));
            return section;
        }

        section.Blocks.Add(kv);
        return section;
    }

    private static ReportSection? BuildDeploymentAndDelivery(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        if (!TryGetAnnotationMap(plan.Annotations, "deployment", out var dep))
        {
            if (!options.ShowEmptySections)
                return null;
        }

        var section = new ReportSection
        {
            Heading = "Deployment + Delivery",
            Level = 2,
            Anchor = "deployment-delivery",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "How the system is deployed and released: topology, ingress/TLS termination, rollout/rollback, and migrations. " +
                        "Model under annotations.deployment (e.g., topology, ingress, tlsTermination, rolloutStrategy, rollbackStrategy, migrationStrategy)."
                }
            }
        };

        if (dep == null)
        {
            section.Blocks.Add(NotModeledCallout("Deployment/delivery"));
            return section;
        }

        var kv = new KeyValueTableBlock();
        foreach (var key in new[] { "topology", "ingress", "tlsTermination", "rolloutStrategy", "rollbackStrategy", "migrationStrategy" })
        {
            if (TryGetString(dep, key, out var value) && !string.IsNullOrWhiteSpace(value))
                kv.Rows.Add(kv.New(key, value));
        }

        if (kv.Rows.Count == 0)
        {
            if (!options.ShowEmptySections)
                return null;
            section.Blocks.Add(NotModeledCallout("Deployment/delivery (values)"));
            return section;
        }

        section.Blocks.Add(kv);
        return section;
    }

    private static ReportSection? BuildApiGovernance(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        if (!TryGetAnnotationMap(plan.Annotations, "apiGovernance", out var api))
        {
            if (!options.ShowEmptySections)
                return null;
        }

        var section = new ReportSection
        {
            Heading = "API Governance",
            Level = 2,
            Anchor = "api-governance",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Policies for interface evolution and protection: versioning, deprecation, rate limiting, and contract ownership. " +
                        "Model under annotations.apiGovernance (e.g., versioningPolicy, deprecationPolicy, rateLimiting, contractOwnership)."
                }
            }
        };

        if (api == null)
        {
            section.Blocks.Add(NotModeledCallout("API governance"));
            return section;
        }

        var kv = new KeyValueTableBlock();
        foreach (var key in new[] { "versioningPolicy", "deprecationPolicy", "rateLimiting", "quotas", "contractOwnership" })
        {
            if (TryGetString(api, key, out var value) && !string.IsNullOrWhiteSpace(value))
                kv.Rows.Add(kv.New(key, value));
        }

        if (kv.Rows.Count == 0)
        {
            if (!options.ShowEmptySections)
                return null;
            section.Blocks.Add(NotModeledCallout("API governance (values)"));
            return section;
        }

        section.Blocks.Add(kv);
        return section;
    }

    private static ReportSection? BuildTenancyModel(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        if (!TryGetAnnotationMap(plan.Annotations, "tenancy", out var ten))
        {
            if (!options.ShowEmptySections)
                return null;
        }

        var section = new ReportSection
        {
            Heading = "Tenancy Model",
            Level = 2,
            Anchor = "tenancy-model",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Details for multi-tenancy and isolation: partitioning, tenant-aware authz, and noisy-neighbor mitigation. " +
                        "Model under annotations.tenancy (e.g., isolationModel, partitioningStrategy, tenantAwareAuthz, noisyNeighborMitigation)."
                }
            }
        };

        if (ten == null)
        {
            section.Blocks.Add(NotModeledCallout("Tenancy model"));
            return section;
        }

        var kv = new KeyValueTableBlock();
        foreach (var key in new[] { "isolationModel", "partitioningStrategy", "tenantAwareAuthz", "noisyNeighborMitigation" })
        {
            if (TryGetString(ten, key, out var value) && !string.IsNullOrWhiteSpace(value))
                kv.Rows.Add(kv.New(key, value));
        }

        if (kv.Rows.Count == 0)
        {
            if (!options.ShowEmptySections)
                return null;
            section.Blocks.Add(NotModeledCallout("Tenancy model (values)"));
            return section;
        }

        section.Blocks.Add(kv);
        return section;
    }

    private static ReportSection BuildProcessingFunctionality(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "Processing",
            Level = 2,
            Anchor = "processing",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This area summarizes non-UI (back-end) capabilities. It gathers declared service capabilities for processing, orchestration, automation, and internal APIs."
                }
            }
        };

        var processingServices = plan.Entities.Services
            .Where(s => !IsUserInterfaceService(plan, s))
            .OrderBy(s => s.Name)
            .ToList();

        if (processingServices.Count == 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Info,
                Title = "No processing services detected",
                Message = "No non-UI services were found in the compiled plan."
            });
            return section;
        }

        var capabilityToServices = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var servicesWithCapabilities = 0;

        foreach (var svc in processingServices)
        {
            var caps = GetCapabilities(svc);
            if (caps.Count > 0) servicesWithCapabilities++;

            foreach (var cap in caps)
            {
                if (string.IsNullOrWhiteSpace(cap)) continue;

                if (!capabilityToServices.TryGetValue(cap, out var set))
                {
                    set = new(StringComparer.Ordinal);
                    capabilityToServices[cap] = set;
                }

                set.Add(svc.Name);
            }
        }

        section.Blocks.Add(new BulletListBlock
        {
            Items =
            {
                new ListItem { Text = $"Processing services detected: {processingServices.Count}" },
                new ListItem { Text = $"Processing services declaring capabilities: {servicesWithCapabilities}" },
                new ListItem { Text = $"Unique processing capabilities declared: {capabilityToServices.Count}" },
            }
        });

        var summary = new TableBlock
        {
            Headers = { "Capability", "Coverage", "Services" }
        };

        foreach (var cap in capabilityToServices
                     .OrderByDescending(kvp => kvp.Value.Count)
                     .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
                     .Take(30))
        {
            var services = cap.Value.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var servicesText = string.Join(", ", services.Take(3));
            if (services.Count > 3)
                servicesText += (servicesText.Length == 0 ? string.Empty : ", ") + $"+{services.Count - 3} more";

            summary.Rows.Add(new TableRow
            {
                Cells =
                {
                    cap.Key,
                    $"{cap.Value.Count}/{processingServices.Count}",
                    servicesText
                }
            });
        }

        section.Blocks.Add(new ParagraphBlock { Text = "Aggregated processing capability summary (top 30 by coverage):" });
        section.Blocks.Add(summary);

        return section;
    }

    private static ReportSection BuildUserInterfacesFunctionality(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "User Interfaces",
            Level = 2,
            Anchor = "user-interfaces",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This area gathers the capabilities declared on web sites / UI services and produces both a per-UI inventory and an aggregated summary."
                }
            }
        };

        var uiServices = plan.Entities.Services
            .Where(s => IsUserInterfaceService(plan, s))
            .OrderBy(s => s.Name)
            .ToList();

        if (uiServices.Count == 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Warning,
                Title = "No user interfaces detected",
                Message =
                    "No services looked like web sites / UIs based on exports, consumers, and platform heuristics. " +
                    "If you have UI services, ensure they export a web endpoint (e.g., export name contains 'web' or consumers include '.../browser') " +
                    "or add service.annotations.ui: true."
            });
            return section;
        }

        var capabilityToUis = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var uiWithCapabilities = 0;

        foreach (var ui in uiServices)
        {
            var caps = GetCapabilities(ui);
            if (caps.Count > 0) uiWithCapabilities++;

            foreach (var cap in caps)
            {
                if (string.IsNullOrWhiteSpace(cap)) continue;

                if (!capabilityToUis.TryGetValue(cap, out var set))
                {
                    set = new(StringComparer.Ordinal);
                    capabilityToUis[cap] = set;
                }

                set.Add(ui.Name);
            }
        }

        section.Blocks.Add(new BulletListBlock
        {
            Items =
            {
                new ListItem { Text = $"UI services detected: {uiServices.Count}" },
                new ListItem { Text = $"UI services declaring capabilities: {uiWithCapabilities}" },
                new ListItem { Text = $"Unique UI capabilities declared: {capabilityToUis.Count}" },
            }
        });

        if (capabilityToUis.Count == 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Info,
                Title = "No UI capabilities declared",
                Message =
                    "UI services were detected, but none declare capabilities. Add service.annotations.capabilities to each UI service to make this summary useful."
            });
            return section;
        }

        var summary = new TableBlock
        {
            Headers = { "Capability", "Coverage", "UIs" }
        };

        foreach (var cap in capabilityToUis
                     .OrderByDescending(kvp => kvp.Value.Count)
                     .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
                     .Take(30))
        {
            var uis = cap.Value.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var uisText = string.Join(", ", uis.Take(3));
            if (uis.Count > 3)
                uisText += (uisText.Length == 0 ? string.Empty : ", ") + $"+{uis.Count - 3} more";

            summary.Rows.Add(new TableRow
            {
                Cells =
                {
                    cap.Key,
                    $"{cap.Value.Count}/{uiServices.Count}",
                    uisText
                }
            });
        }

        section.Blocks.Add(summary);

        return section;
    }

    private static ReportSection BuildSystemContext(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "System Context",
            Level = 1,
            Anchor = "system-context",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section highlights external actors and external systems at the boundary of the application. " +
                        "It is a lightweight system context view (who calls in, and what the system depends on). " +
                        "External consumers can be human end users (browser/mobile), partner/customer integrations, scheduled jobs, and identity providers (IdPs)—" +
                        "anything outside the modeled services that calls an export."
                }
            }
        };

        var primaryUsers = plan.DeliveryModel?.PrimaryUsers
            ?.Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(u => u)
            .ToList() ?? [];
        
        var terminalEntryExports = FilterToTerminalEntryExports(plan, GetEntryExports(plan));

        var consumerGroups = terminalEntryExports
            .Where(i => i.Consumers is { Count: > 0 })
            .SelectMany(i => i.Consumers!.Select(c => new { Consumer = c, Export = i }))
            .Where(x => !string.IsNullOrWhiteSpace(x.Consumer))
            .GroupBy(x => x.Consumer)
            .OrderBy(g => g.Key);

        if ((primaryUsers.Count > 0) && !consumerGroups.Any())
        {
            section.Blocks.Add(new ParagraphBlock
            {
                Text =
                    "Declared primary users (deliveryModel.primaryUsers) describe the intended audiences of the system. " +
                    "For concrete entry-point mapping, these should also appear as export consumers (exports.*.consumers) where applicable."
            });

            var primaryUsersTable = new TableBlock
            {
                Headers = { "Primary User", "Scope" }
            };

            foreach (var user in primaryUsers)
            {
                var scope = user.StartsWith("external://", StringComparison.OrdinalIgnoreCase)
                    ? "external"
                    : user.StartsWith("internal://", StringComparison.OrdinalIgnoreCase)
                        ? "internal"
                        : "unspecified";

                primaryUsersTable.Rows.Add(new TableRow
                {
                    Cells =
                    {
                        user,
                        scope
                    }
                });
            }

            section.Blocks.Add(primaryUsersTable);
        }

        if (consumerGroups.Any())
        {
            var consumersTable = new TableBlock
            {
                Headers = { "External Consumer", "Services", "Exports", "Notes" }
            };

            foreach (var grp in consumerGroups)
            {
                var serviceIds = grp.Select(x => x.Export.ServiceRef).Distinct().OrderBy(x => x).ToList();
                var exportIds = grp.Select(x => x.Export.Id).Distinct().OrderBy(x => x).ToList();

                var sampleExports = grp
                    .Select(x => $"{x.Export.ServiceRef}/{x.Export.Name}")
                    .Distinct()
                    .OrderBy(x => x)
                    .Take(3)
                    .ToList();

                consumersTable.Rows.Add(new TableRow
                {
                    Cells =
                    {
                        grp.Key,
                        serviceIds.Count.ToString(),
                        exportIds.Count.ToString(),
                        sampleExports.Count == 0 ? string.Empty : $"e.g., {string.Join(", ", sampleExports)}"
                    }
                });
            }

            section.Blocks.Add(new ParagraphBlock
            {
                Text =
                    "External consumers are declared on exports via exports.*.consumers. " +
                    "To reduce duplication, only consumers attached to terminal entry points are shown (if a service is reachable from another entry point, it is omitted here)."
            });
            section.Blocks.Add(consumersTable);
        }
        else
        {
            if (primaryUsers.Any(u => u.StartsWith("external://", StringComparison.OrdinalIgnoreCase)))
            {
                section.Blocks.Add(new ParagraphBlock
                {
                    Text =
                        "No external consumers are declared on exports yet, but external primary users are declared in deliveryModel.primaryUsers. " +
                        "Consider adding exports.*.consumers entries that map those users to specific inbound interfaces."
                });
            }

            section.Blocks.Add(new ParagraphBlock
            {
                Text = "No external consumers are declared. If this system has inbound actors (users, partners, IdPs, schedulers), model them via exports.*.consumers."
            });
        }

        // External systems: treat resources on SaaS platforms (or explicitly external kinds) as boundary dependencies.
        var resources = plan.Entities.Resources;
        var externalResources = resources
            .Where(r =>
            {
                var platformCategory = string.IsNullOrWhiteSpace(r.PlatformRef)
                    ? null
                    : plan.Entities.Platforms.FirstOrDefault(p => p.Id == r.PlatformRef)?.Category;

                if (string.Equals(platformCategory, "saas", StringComparison.OrdinalIgnoreCase)) return true;
                return (r.ResourceKind?.Contains("external", StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .OrderBy(r => r.Name)
            .ToList();

        if (externalResources.Count > 0)
        {
            var externalSystemsTable = new TableBlock
            {
                Headers = { "External System", "Kind", "Platform", "Used By (#services)", "Description" }
            };

            foreach (var resource in externalResources)
            {
                var usedByServiceIds = plan.Dependencies
                    .Where(d => d.Kind == "service-to-resource" && d.To == resource.Id)
                    .Select(d => d.From)
                    .Distinct()
                    .ToList();

                externalSystemsTable.Rows.Add(new TableRow
                {
                    Cells =
                    {
                        resource.Name,
                        resource.ResourceKind ?? string.Empty,
                        resource.PlatformRef ?? string.Empty,
                        usedByServiceIds.Count.ToString(),
                        resource.Description ?? string.Empty
                    }
                });
            }

            section.Blocks.Add(new ParagraphBlock
            {
                Text = "External systems are inferred from resources that run on SaaS platforms (or have external-* kinds)."
            });
            section.Blocks.Add(externalSystemsTable);
        }

        return section;
    }

    private static ReportSection BuildEnvironmentOverview(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "Environment Overview",
            Level = 1,
            Anchor = "environment-overview",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Environments describe the SDLC/runtime progression for deployments (e.g., dev → uat → prod). " +
                        "Promotion flow is modeled via environments.*.promotesTo."
                }
            }
        };

        if (plan.Entities.Environments.Count == 0)
        {
            section.Blocks.Add(new ParagraphBlock
            {
                Text = "No environments are defined in the compiled plan."
            });
            return section;
        }

        var table = new TableBlock
        {
            Headers = { "Environment", "Promotes To", "Description" }
        };

        foreach (var env in EnvironmentPromotionOrdering.Order(plan.Entities.Environments))
        {
            var promotesTo = env.PromotesTo is { Count: > 0 }
                ? string.Join(", ", env.PromotesTo.Distinct().OrderBy(x => x))
                : string.Empty;

            table.Rows.Add(new TableRow
            {
                Cells =
                {
                    env.Name,
                    promotesTo,
                    env.Description ?? string.Empty
                }
            });
        }

        section.Blocks.Add(table);
        return section;
    }

    private ReportSection BuildDataUsageOverview(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "Data Usage",
            Level = 1,
            Anchor = "data-usage",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section summarizes where data classifications appear in the architecture. " +
                        "Classifications can be attached to exports (data in motion) and resources (data at rest / external systems)."
                }
            }
        };

        var dataClasses = plan.Entities.DataClasses.OrderBy(d => d.Name).ToList();
        if (dataClasses.Count == 0)
        {
            section.Blocks.Add(new ParagraphBlock
            {
                Text = "No data classifications are defined in this plan."
            });
            return section;
        }

        var usageTable = new TableBlock
        {
            Headers = { "Classification", "Exports (#)", "Resources (#)", "Examples" }
        };

        foreach (var dc in dataClasses)
        {
            var exportRefs = plan.Interfaces
                .Where(i => i.DataClassRefs != null && i.DataClassRefs.Contains(dc.Id))
                .Select(i => $"{i.ServiceRef}/{i.Name}")
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var resourceRefs = plan.Entities.Resources
                .Where(r => r.DataClassRefs != null && r.DataClassRefs.Contains(dc.Id))
                .Select(r => r.Name)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var examples = exportRefs.Concat(resourceRefs)
                .Take(3)
                .ToList();

            usageTable.Rows.Add(new TableRow
            {
                Cells =
                {
                    dc.Name,
                    exportRefs.Count.ToString(),
                    resourceRefs.Count.ToString(),
                    examples.Count == 0 ? string.Empty : $"e.g., {string.Join(", ", examples)}"
                }
            });
        }

        section.Blocks.Add(usageTable);
        return section;
    }

    // ------------------------------------------------------------
    // Sections
    // ------------------------------------------------------------

    private static ReportSection BuildIntendedUse()
    {
        return new ReportSection
        {
            Heading = "Intended Use",
            Level = 1,
            Anchor = "intended-use",
            Blocks =
            {
                new CalloutBlock
                {
                    CalloutType = CalloutKind.Info,
                    Title = "Who should read this report?",
                    Message = "This report is intended for engineering teams, architects, and onboarding developers to understand the system structure and dependencies."
                }
            }
        };
    }

    private ReportSection BuildExecutiveSummary(CompiledFlightPlan plan)
    {
        return new ReportSection
        {
            Heading = "Executive Summary",
            Level = 1,
            Anchor = "executive-summary",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        $"This report summarizes the {plan.Application?.Name} system using the compiled flight plan. " +
                        "It highlights the platform foundations, the service landscape, shared data contracts, and supporting resources used by the system."
                },

                new ParagraphBlock
                {
                    Text =
                        "How to read: services are compute units, resources are shared infrastructure/external systems, and exports are named interfaces exposed by services. " +
                        "Dependencies describe how services call exports or use resources."
                },

                ArchitectureOverviewReportGeneratorHelpers.BuildExecutiveSummaryTable(plan, _options)
            }
        };
    }

    private ReportSection BuildResourceOverview(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "Resource Overview",
            Level = 1,
            Anchor = "resource-overview"
        };

        if (plan.Entities.Resources.Count == 0)
        {
            section.Blocks.Add(new ParagraphBlock
            {
                Text =
                    "No supporting resources are defined in this flight plan. " +
                    "If your architecture relies on shared infrastructure (databases, queues, caches, external APIs), consider modeling them as resources to improve traceability."
            });
            return section;
        }

        section.Blocks.Add(new ParagraphBlock
        {
            Text =
                "Resources represent shared infrastructure and external systems used by services (e.g., databases, queues, caches, and third-party APIs). " +
                "They are grouped below by kind to make usage patterns easier to scan."
        });

        var resourcesByKind = plan.Entities.Resources
            .GroupBy(r => r.ResourceKind ?? "unknown")
            .OrderBy(g => g.Key);

        foreach (var kindGroup in resourcesByKind)
        {
            var kindSection = new ReportSection
            {
                Heading = $"Kind: {kindGroup.Key}",
                Level = 2,
                Blocks =
                {
                    new ParagraphBlock
                    {
                        Text = $"{kindGroup.Count()} resource(s) of kind '{kindGroup.Key}'. Grouped by platform."
                    }
                }
            };

            var byPlatform = kindGroup
                .GroupBy(r => r.PlatformRef ?? "(none)")
                .OrderBy(g => g.Key);

            foreach (var platformGroup in byPlatform)
            {
                var table = new TableBlock
                {
                    Headers = { "Resource", "Platform", "Zone", "Used By (#services)", "Handles Data", "Description" }
                };

                foreach (var resource in platformGroup.OrderBy(r => r.Name))
                {
                    var usedByServiceIds = plan.Dependencies
                        .Where(d => d.Kind == "service-to-resource" && d.To == resource.Id)
                        .Select(d => d.From)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToList();

                    var handlesData = resource.DataClassRefs is { Count: > 0 }
                        ? $"yes ({resource.DataClassRefs.Count})"
                        : "no";

                    table.Rows.Add(new TableRow
                    {
                        Cells =
                        {
                            CatalogLinkHelper.ResourceLink(resource),
                            resource.PlatformRef ?? string.Empty,
                            resource.ZoneRef ?? string.Empty,
                            usedByServiceIds.Count.ToString(),
                            handlesData,
                            resource.Description ?? string.Empty
                        }
                    });
                }

                kindSection.Children.Add(new ReportSection
                {
                    Heading = $"Platform: {platformGroup.Key}",
                    Level = 3,
                    Blocks =
                    {
                        new ParagraphBlock
                        {
                            Text = $"{platformGroup.Count()} resource(s) on platform '{platformGroup.Key}'."
                        },
                        table
                    }
                });
            }

            section.Children.Add(kindSection);
        }

        return section;
    }

    private ReportSection BuildPlatformOverview(CompiledFlightPlan plan)
    {
        var table = new TableBlock
        {
            Headers = { "Platform", "Type", "Description" }
        };

        foreach (var platform in plan.Entities.Platforms.OrderBy(p => p.Name))
        {
            table.Rows.Add(new TableRow
            {
                Cells =
                {
                    platform.Name,
                    platform.Category,
                    platform.Description ?? string.Empty
                }
            });
        }

        return new ReportSection
        {
            Heading = "Platform Overview",
            Level = 1,
            Anchor = "platform-overview",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Platforms describe the technology foundations the architecture depends on (cloud providers, managed services, runtimes, and SaaS). " +
                        "This section lists the platforms referenced by the compiled plan."
                },
                table
            }
        };
    }

    private ReportSection BuildServiceOverview(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "Service Overview",
            Level = 1,
            Anchor = "service-overview"
        };

        section.Blocks.Add(new ParagraphBlock
        {
            Text =
                "Services are the primary compute units in the system. The summary table provides a quick inventory. " +
                "For detailed exports and dependencies per service, see the published [Service Catalog](service-catalog/index.html)."
        });

        var allServicesTable = new TableBlock
        {
            Headers = { "Service", "Repo", "Owner", "Platform", "Zone", "Exports", "Outbound", "Inbound", "Description" }
        };

        foreach (var svc in plan.Entities.Services.OrderBy(s => s.Name))
        {
            var exportCount = plan.Interfaces.Count(i => i.ServiceRef == svc.Id);
            var outboundCount = plan.Dependencies.Count(d => d.From == svc.Id);
            var inboundCount = plan.Dependencies.Count(d => d.Kind == "service-to-service" && IsServiceExportTarget(d.To, svc.Id));

            allServicesTable.Rows.Add(new TableRow
            {
                Cells =
                {
                    CatalogLinkHelper.ServiceLink(svc),
                    FormatRepoLinkCell(svc.RepoUrl),
                    svc.OwnerRef ?? string.Empty,
                    svc.PlatformRef ?? string.Empty,
                    svc.ZoneRef ?? string.Empty,
                    exportCount.ToString(),
                    outboundCount.ToString(),
                    inboundCount.ToString(),
                    svc.Description ?? string.Empty
                }
            });
        }

        section.Blocks.Add(new ParagraphBlock
        {
            Text = "Summary of all services in the compiled plan, including export and dependency counts."
        });
        section.Blocks.Add(allServicesTable);

        return section;
    }

    private ReportSection? BuildToolchainOverview(CompiledFlightPlan plan)
    {
        var toolingById = plan.Entities.Tooling
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToDictionary(t => t.Id, StringComparer.Ordinal);

        string FormatTool(string? toolingId)
        {
            if (string.IsNullOrWhiteSpace(toolingId)) return string.Empty;
            if (!toolingById.TryGetValue(toolingId.Trim(), out var tool)) return toolingId.Trim();

            var kind = tool.Kind ?? string.Empty;
            var provider = tool.Provider ?? string.Empty;
            var details = string.Join(", ", new[] { kind, provider }.Where(s => !string.IsNullOrWhiteSpace(s)));

            var label = tool.Name;
            if (!string.IsNullOrWhiteSpace(tool.Url))
                label = $"[{label}]({tool.Url})";

            return string.IsNullOrWhiteSpace(details) ? label : $"{label} ({details})";
        }

        var defaultToolchain = plan.Toolchain;
        var hasDefaultToolchain = defaultToolchain != null && defaultToolchain.Count > 0;

        var servicesWithOverrides = plan.Entities.Services
            .Where(s => s.Toolchain != null && s.Toolchain.Count > 0)
            .OrderBy(s => s.Name)
            .ToList();

        if (!hasDefaultToolchain && servicesWithOverrides.Count == 0)
        {
            if (!_options.ShowEmptySections)
                return null;
        }

        var section = new ReportSection
        {
            Heading = "Toolchain Overview",
            Level = 1,
            Anchor = "toolchain-overview",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section summarizes the default lifecycle toolchain for the application (repo/work tracking/build/deploy/etc) and any service-level overrides. " +
                        "Model the default under the top-level toolchain: map, and overrides under each service.toolchain: map."
                }
            }
        };

        if (!hasDefaultToolchain && servicesWithOverrides.Count == 0)
        {
            section.Blocks.Add(NotModeledCallout("Toolchain"));
            return section;
        }

        if (hasDefaultToolchain)
        {
            var defaultsTable = new TableBlock
            {
                Headers = { "Lifecycle step", "Default tool" }
            };

            foreach (var (k, toolingId) in defaultToolchain!
                         .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                defaultsTable.Rows.Add(new TableRow
                {
                    Cells =
                    {
                        k,
                        FormatTool(toolingId)
                    }
                });
            }

            section.Blocks.Add(defaultsTable);
        }
        else
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Info,
                Title = "No default toolchain",
                Message =
                    "No top-level toolchain is defined. Service-level overrides may still be present, but services without overrides will have no assumed default tool for a lifecycle step."
            });
        }

        if (servicesWithOverrides.Count > 0)
        {
            section.Blocks.Add(new ParagraphBlock
            {
                Text = "Service-level overrides are listed below (only services that override defaults are shown)."
            });

            var overridesTable = new TableBlock
            {
                Headers = { "Service", "Lifecycle step", "Override tool", "Default tool" }
            };

            foreach (var svc in servicesWithOverrides)
            {
                foreach (var (k, toolingId) in svc.Toolchain!
                             .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string? defaultId = null;
                    if (defaultToolchain != null)
                        defaultToolchain.TryGetValue(k, out defaultId);

                    overridesTable.Rows.Add(new TableRow
                    {
                        Cells =
                        {
                            svc.Name,
                            k,
                            FormatTool(toolingId),
                            FormatTool(defaultId)
                        }
                    });
                }
            }

            section.Blocks.Add(overridesTable);
        }

        return section;
    }

    private ReportSection? BuildToolingOverview(CompiledFlightPlan plan)
    {
        if (plan.Entities.Tooling.Count == 0)
        {
            if (!_options.ShowEmptySections)
                return null;
        }

        var section = new ReportSection
        {
            Heading = "Tooling Overview",
            Level = 1,
            Anchor = "tooling-overview",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section lists development and operational tooling that supports the system lifecycle (source control, CI/CD, observability, on-call, work tracking, etc.). " +
                        "Model these under the top-level tooling: map."
                }
            }
        };

        if (plan.Entities.Tooling.Count == 0)
        {
            section.Blocks.Add(NotModeledCallout("Tooling"));
            return section;
        }

        var table = new TableBlock
        {
            Headers = { "Tool", "Kind", "Provider", "Organization", "Zone", "Owner", "URL" }
        };

        foreach (var tool in plan.Entities.Tooling.OrderBy(t => t.Name))
        {
            var kind = tool is ToolingEntity te ? te.Kind : null;
            var provider = tool is ToolingEntity te2 ? te2.Provider : null;
            var org = tool is ToolingEntity te3 ? te3.Organization : null;
            var url = tool is ToolingEntity te4 ? te4.Url : null;

            table.Rows.Add(new TableRow
            {
                Cells =
                {
                    tool.Name,
                    kind ?? string.Empty,
                    provider ?? string.Empty,
                    org ?? string.Empty,
                    tool.ZoneRef ?? string.Empty,
                    tool.OwnerRef ?? string.Empty,
                    string.IsNullOrWhiteSpace(url) ? string.Empty : $"[link]({url})"
                }
            });
        }

        section.Blocks.Add(table);
        return section;
    }

    private static string FormatRepoLinkCell(string? repoUrl)
    {
        var trimmed = (repoUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return string.Empty;

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

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Uri.UnescapeDataString(s))
            .ToArray();

        if (segments.Length == 0) return null;

        // Repo name parsed from end of URL.
        var host = (uri.Host ?? string.Empty).Trim().ToLowerInvariant();

        if (host is "dev.azure.com" || host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            var gitIndex = Array.FindIndex(segments, s => string.Equals(s, "_git", StringComparison.OrdinalIgnoreCase));
            if (gitIndex >= 0 && gitIndex + 1 < segments.Length)
            {
                return StripGitSuffix(segments[gitIndex + 1]);
            }
        }

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

    private static bool TrySplitExportId(string exportId, out string serviceId, out string exportName)
    {
        serviceId = string.Empty;
        exportName = string.Empty;

        if (string.IsNullOrWhiteSpace(exportId)) return false;

        var idx = exportId.IndexOf('/');
        if (idx <= 0 || idx >= exportId.Length - 1) return false;

        serviceId = exportId.Substring(0, idx);
        exportName = exportId.Substring(idx + 1);
        return true;
    }

    private static bool IsUserInterfaceService(CompiledFlightPlan plan, ServiceEntity svc)
    {
        if (svc.Annotations != null)
        {
            if (TryGetAnnotationValue(svc.Annotations, "ui", out var uiFlag) && uiFlag is bool b && b)
                return true;

            if (TryGetAnnotationValue(svc.Annotations, "userInterface", out var uiFlag2) && uiFlag2 is bool b2 && b2)
                return true;
        }

        var platform = (svc.PlatformRef ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(platform) &&
            (platform.Contains("angular", StringComparison.OrdinalIgnoreCase) ||
             platform.Contains("react", StringComparison.OrdinalIgnoreCase) ||
             platform.Contains("vue", StringComparison.OrdinalIgnoreCase) ||
             platform.Contains("svelte", StringComparison.OrdinalIgnoreCase) ||
             platform.Contains("mvc", StringComparison.OrdinalIgnoreCase) ||
             platform.Contains("web", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var exports = plan.Interfaces.Where(i => i.ServiceRef == svc.Id).ToList();
        if (exports.Count == 0) return false;

        if (exports.Any(e => (e.Name ?? string.Empty).Contains("web", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (exports.Any(e => e.Consumers is { Count: > 0 } && e.Consumers.Any(c => (c ?? string.Empty).Contains("browser", StringComparison.OrdinalIgnoreCase))))
            return true;

        // if ((svc.Name ?? string.Empty).Contains("web", StringComparison.OrdinalIgnoreCase) &&
        //     exports.Any(e => string.Equals(e.Protocol, "https", StringComparison.OrdinalIgnoreCase) || string.Equals(e.Protocol, "http", StringComparison.OrdinalIgnoreCase)))
        // {
        //     return true;
        // }

        return false;
    }

    private static IReadOnlyList<string> GetCapabilities(ServiceEntity svc)
    {
        if (svc.Annotations == null || svc.Annotations.Count == 0) return Array.Empty<string>();

        if (!TryGetAnnotationValue(svc.Annotations, "capabilities", out var value) &&
            !TryGetAnnotationValue(svc.Annotations, "functions", out value) &&
            !TryGetAnnotationValue(svc.Annotations, "systemFunctionality", out value))
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();

        switch (value)
        {
            case string s when !string.IsNullOrWhiteSpace(s):
                list.Add(s.Trim());
                break;
            case IEnumerable<object> seq:
                foreach (var item in seq)
                {
                    if (item is null) continue;
                    var text = item.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) list.Add(text.Trim());
                }

                break;
        }

        return list
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryGetAnnotationValue(IReadOnlyDictionary<string, object> annotations, string key, out object? value)
    {
        value = null;
        if (annotations.TryGetValue(key, out var direct))
        {
            value = direct;
            return true;
        }

        var match = annotations.FirstOrDefault(kvp => string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(match.Key))
        {
            value = match.Value;
            return true;
        }

        return false;
    }

    private static string FormatShortList(IReadOnlyList<string> items, int take)
    {
        if (items.Count == 0) return string.Empty;

        var shown = items.Take(take).ToList();
        var text = string.Join(", ", shown);
        if (items.Count > take)
            text += (text.Length == 0 ? string.Empty : ", ") + $"+{items.Count - take} more";

        return text;
    }

    private static CalloutBlock NotModeledCallout(string area)
    {
        return new CalloutBlock
        {
            CalloutType = CalloutKind.Info,
            Title = "Not modeled yet",
            Message =
                $"{area} is not modeled in the flight plan yet. Add it under top-level annotations to make this section actionable."
        };
    }

    private static bool TryGetAnnotationMap(Dictionary<string, object>? annotations, string key, out Dictionary<string, object> map)
    {
        map = new(StringComparer.Ordinal);

        if (annotations == null || annotations.Count == 0) return false;

        if (!annotations.TryGetValue(key, out var direct))
        {
            var match = annotations.FirstOrDefault(kvp => string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(match.Key)) return false;
            direct = match.Value;
        }

        if (direct is Dictionary<string, object> dict)
        {
            map = dict;
            return true;
        }

        if (direct is IReadOnlyDictionary<string, object> ro)
        {
            map = ro.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
            return true;
        }

        return false;
    }

    private static bool TryGetAnnotationMapList(Dictionary<string, object>? annotations, string key, out List<Dictionary<string, object>> values)
    {
        values = [];

        if (annotations == null || annotations.Count == 0) return false;

        if (!annotations.TryGetValue(key, out var direct))
        {
            var match = annotations.FirstOrDefault(kvp => string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(match.Key)) return false;
            direct = match.Value;
        }

        if (direct is not System.Collections.IEnumerable seq || direct is string) return false;

        foreach (var item in seq)
        {
            switch (item)
            {
                case Dictionary<string, object> dict:
                    values.Add(dict);
                    break;
                case IReadOnlyDictionary<string, object> ro:
                    values.Add(ro.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal));
                    break;
            }
        }

        return values.Count > 0;
    }

    private static bool TryGetString(IReadOnlyDictionary<string, object> map, string key, out string? value)
    {
        value = null;
        if (map.TryGetValue(key, out var direct))
        {
            value = ConvertAnnotationValueToString(direct);
            return true;
        }

        var match = map.FirstOrDefault(kvp => string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(match.Key))
        {
            value = ConvertAnnotationValueToString(match.Value);
            return true;
        }

        return false;
    }

    private static string? ConvertAnnotationValueToString(object? value)
    {
        if (value is null) return null;
        if (value is string s) return s;

        // For lists, join items so tables don't show type names.
        if (value is System.Collections.IEnumerable seq && value is not IReadOnlyDictionary<string, object> && value is not Dictionary<string, object>)
        {
            var parts = new List<string>();
            foreach (var item in seq)
            {
                if (item is null) continue;
                var text = item.ToString();
                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
            }

            return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
        }

        return value.ToString();
    }

    private static bool TryGetMapList(IReadOnlyDictionary<string, object> map, string key, out List<Dictionary<string, object>> values)
    {
        values = [];

        if (!map.TryGetValue(key, out var direct))
        {
            var match = map.FirstOrDefault(kvp => string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(match.Key)) return false;
            direct = match.Value;
        }

        if (direct is not System.Collections.IEnumerable seq || direct is string) return false;

        foreach (var item in seq)
        {
            switch (item)
            {
                case Dictionary<string, object> dict:
                    values.Add(dict);
                    break;
                case IReadOnlyDictionary<string, object> ro:
                    values.Add(ro.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal));
                    break;
            }
        }

        return values.Count > 0;
    }

    private static bool TryGetStringList(IReadOnlyDictionary<string, object> map, string key, out List<string> values)
    {
        values = [];

        if (!map.TryGetValue(key, out var direct))
        {
            var match = map.FirstOrDefault(kvp => string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(match.Key)) return false;
            direct = match.Value;
        }

        switch (direct)
        {
            case IEnumerable<object> seq:
                foreach (var item in seq)
                {
                    if (item is null) continue;
                    var text = item.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) values.Add(text.Trim());
                }

                break;
            case string s when !string.IsNullOrWhiteSpace(s):
                values.Add(s.Trim());
                break;
            default:
                return false;
        }

        values = values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        return values.Count > 0;
    }

    private static bool IsServiceExportTarget(string to, string serviceId)
    {
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(serviceId)) return false;
        return to.StartsWith(serviceId + "/", StringComparison.Ordinal);
    }

    private static List<ServiceExport> GetEntryExports(CompiledFlightPlan plan)
    {
        return plan.Interfaces
            .Where(i =>
                string.Equals(i.Visibility, "external", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.Visibility, "public", StringComparison.OrdinalIgnoreCase) ||
                (i.Consumers is { Count: > 0 }))
            .OrderBy(i => i.ServiceRef)
            .ThenBy(i => i.Name)
            .ToList();
    }

    private static List<ServiceExport> FilterToTerminalEntryExports(CompiledFlightPlan plan, List<ServiceExport> entryExports)
    {
        if (entryExports.Count <= 1) return entryExports;

        var entryServiceIds = entryExports
            .Select(e => e.ServiceRef)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (entryServiceIds.Count <= 1) return entryExports;

        var adjacency = BuildServiceToServiceAdjacency(plan);

        // Mark any service that is reachable from an entry service (excluding itself).
        var reachableFromAnyEntry = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entryServiceId in entryServiceIds)
        {
            foreach (var reachable in GetReachableServices(entryServiceId, adjacency))
            {
                reachableFromAnyEntry.Add(reachable);
            }
        }

        // A terminal entry service is one that is not reachable from any other entry service.
        var terminalEntryServiceIds = entryServiceIds
            .Where(svcId => !reachableFromAnyEntry.Contains(svcId))
            .ToHashSet(StringComparer.Ordinal);

        // If everything is mutually reachable (cycles) or otherwise filtered out, fall back to the unfiltered list.
        if (terminalEntryServiceIds.Count == 0) return entryExports;

        // Always keep exports with explicit external consumers, even if the service is downstream.
        // This avoids hiding integration-specific entry points (e.g., SCIM provisioning) when a gateway sits behind another front door.
        var filtered = entryExports
            .Where(e => terminalEntryServiceIds.Contains(e.ServiceRef) || (e.Consumers is { Count: > 0 }))
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        return filtered.Count == 0 ? entryExports : filtered;
    }

    private static Dictionary<string, List<string>> BuildServiceToServiceAdjacency(CompiledFlightPlan plan)
    {
        var outbound = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var dep in plan.Dependencies)
        {
            if (dep.Kind != "service-to-service") continue;
            if (string.IsNullOrWhiteSpace(dep.From) || string.IsNullOrWhiteSpace(dep.To)) continue;

            var toServiceId = dep.To;
            if (TrySplitExportId(dep.To, out var parsedServiceId, out _))
            {
                toServiceId = parsedServiceId;
            }

            if (string.IsNullOrWhiteSpace(toServiceId)) continue;

            if (!outbound.TryGetValue(dep.From, out var list))
            {
                list = [];
                outbound[dep.From] = list;
            }

            list.Add(toServiceId);
        }

        return outbound;
    }

    private static IEnumerable<string> GetReachableServices(string startServiceId, Dictionary<string, List<string>> outbound)
    {
        if (string.IsNullOrWhiteSpace(startServiceId)) yield break;

        var visited = new HashSet<string>(StringComparer.Ordinal) { startServiceId };
        var queue = new Queue<string>();
        queue.Enqueue(startServiceId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!outbound.TryGetValue(current, out var next)) continue;

            foreach (var to in next)
            {
                if (string.IsNullOrWhiteSpace(to)) continue;
                if (!visited.Add(to)) continue;

                yield return to;
                queue.Enqueue(to);
            }
        }
    }

    private ReportSection BuildDataClassOverview(CompiledFlightPlan plan)
    {
        var table = new TableBlock
        {
            Headers = { "Classification", "Description" }
            //Headers = { "Data Class", "Scope", "Description" }
        };

        foreach (var dc in plan.Entities.DataClasses.OrderBy(d => d.Name))
        {
            table.Rows.Add(new TableRow
            {
                Cells =
                {
                    dc.Name,
                    //dc.Scope.ToString(),
                    dc.Description ?? string.Empty
                }
            });
        }

        return new ReportSection
        {
            Heading = "Data Classification Overview",
            Level = 1,
            Anchor = "data-classification-overview",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Classifications label the kinds of data handled by exports and resources (for example: PII, PCI, PHI, Confidential). " +
                        "Only classifications referenced by an entity are included in the compiled view, so this section reflects what is actually in use."
                },
                table
            }
        };
    }
}

// ------------------------------------------------------------
// Small helpers
// ------------------------------------------------------------

internal static class KeyValueRowExtensions
{
    public static KeyValueRow New(this KeyValueTableBlock _, string key, string value)
        => new() { Key = key, Value = value };
}

file static class ArchitectureOverviewReportGeneratorHelpers
{
    public static KeyValueTableBlock BuildExecutiveSummaryTable(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        var table = new KeyValueTableBlock();
        
        if (!string.IsNullOrWhiteSpace(plan.Application?.Name))
            table.Rows.Add(table.New("Application Name", plan.Application.Name));
        else if (options.ShowEmptySections)
            table.Rows.Add(table.New("Application Name", "Not modeled yet"));
        
        if (!string.IsNullOrWhiteSpace(plan.Application?.Type))
            table.Rows.Add(table.New("Application Type", plan.Application.Type));
        else if (options.ShowEmptySections)
            table.Rows.Add(table.New("Application Type", "Not modeled yet"));
        
        if (!string.IsNullOrWhiteSpace(plan.Application?.Domain))
            table.Rows.Add(table.New("Domain", plan.Application.Domain));
        else if (options.ShowEmptySections)
            table.Rows.Add(table.New("Domain", "Not modeled yet"));
        
        if (!string.IsNullOrWhiteSpace(plan.DeliveryModel?.Hosting))
            table.Rows.Add(table.New("Hosting", plan.DeliveryModel.Hosting));
        else if (options.ShowEmptySections)
            table.Rows.Add(table.New("Hosting", "Not modeled yet"));
        
        if (!string.IsNullOrWhiteSpace(plan.DeliveryModel?.ServiceModel))
            table.Rows.Add(table.New("Service Model", plan.DeliveryModel.ServiceModel));
        else if (options.ShowEmptySections)
            table.Rows.Add(table.New("Service Model", "Not modeled yet"));
        
        if (!string.IsNullOrWhiteSpace(plan.DeliveryModel?.Tenancy))
            table.Rows.Add(table.New("Tenancy", plan.DeliveryModel.Tenancy));
        else if (options.ShowEmptySections)
            table.Rows.Add(table.New("Tenancy", "Not modeled yet"));
        
        if (plan.DeliveryModel?.PrimaryUsers is { Count: > 0 })
            table.Rows.Add(table.New("Primary Users", string.Join(", ", plan.DeliveryModel.PrimaryUsers)));
        else if (options.ShowEmptySections)
            table.Rows.Add(table.New("Primary Users", "Not modeled yet"));
        
        table.Rows.Add(table.New("Platforms", plan.Entities.Platforms.Count().ToString()));
        table.Rows.Add(table.New("Services", plan.Entities.Services.Count.ToString()));
        table.Rows.Add(table.New("Resources", plan.Entities.Resources.Count.ToString()));
        table.Rows.Add(table.New("Data Classifications", plan.Entities.DataClasses.Count.ToString()));
        table.Rows.Add(table.New("Environments", plan.Entities.Environments.Count.ToString()));
        
        return table;
    }
}
