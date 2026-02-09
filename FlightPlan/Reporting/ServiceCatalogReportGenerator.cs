namespace FlightPlan.Reporting;

/// <summary>
/// Produces a detailed service catalog intended for audits, onboarding, and architecture inventory.
/// Focuses on services, exports, dependencies, and traceability (owners/repos).
/// </summary>
public sealed class ServiceCatalogReportGenerator : IReportGenerator
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

        var doc = new ReportDocument
        {
            Title = "Service Catalog",
            Subtitle = "Detailed inventory of services, interfaces, and dependencies",
            Metadata = new ReportMetadata
            {
                ApplicationName = plan.Application?.Name,
                Version = plan.Metadata.Format,
                Tags =
                {
                    ["report"] = "service-catalog",
                    ["generated-by"] = "flightplan"
                }
            }
        };

        doc.Sections.Add(BuildAtAGlance(plan));
        doc.Sections.Add(BuildServiceCatalog(plan));
        doc.Sections.Add(BuildEntryPoints(plan));
        doc.Sections.Add(BuildHotspots(plan));

        doc.Findings.AddRange(CollectFindings(plan));

        return doc;
    }

    public ReportDocument GenerateIndex(CompiledFlightPlan plan, Func<ServiceEntity, string> serviceHrefFactory)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (serviceHrefFactory == null) throw new ArgumentNullException(nameof(serviceHrefFactory));

        var doc = new ReportDocument
        {
            Title = "Service Catalog",
            Subtitle = "Service index (one file per service)",
            Metadata = new ReportMetadata
            {
                ApplicationName = plan.Application?.Name,
                Version = plan.Metadata.Format,
                Tags =
                {
                    ["report"] = "service-catalog",
                    ["generated-by"] = "flightplan"
                }
            }
        };

        doc.Sections.Add(BuildAtAGlance(plan));
        doc.Sections.Add(BuildServiceCatalogIndex(plan, serviceHrefFactory));
        doc.Sections.Add(BuildEntryPoints(plan, serviceHrefFactory));
        doc.Sections.Add(BuildHotspots(plan, serviceHrefFactory));
        doc.Findings.AddRange(CollectFindings(plan));

        return doc;
    }

    public ReportDocument GenerateServiceDetail(CompiledFlightPlan plan, ServiceEntity service, string? indexHref)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (service == null) throw new ArgumentNullException(nameof(service));

        var doc = new ReportDocument
        {
            Title = $"Service: {service.Name}",
            Subtitle = "Service catalog detail",
            Metadata = new ReportMetadata
            {
                ApplicationName = plan.Application?.Name,
                Version = plan.Metadata.Format,
                Tags =
                {
                    ["report"] = "service-catalog",
                    ["generated-by"] = "flightplan",
                    ["service"] = service.Id ?? service.Name
                }
            }
        };

        var section = BuildServiceDetailSection(plan, service, indexHref, level: 1);
        doc.Sections.Add(section);

        // Keep findings on the index; avoid duplicating across N files.
        return doc;
    }

    private static ReportSection BuildEntryPoints(CompiledFlightPlan plan, Func<ServiceEntity, string>? serviceHrefFactory = null)
    {
        var section = new ReportSection
        {
            Heading = "Entry Points",
            Level = 1,
            Anchor = "entry-points",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Entry points are exports intended to be called from outside the modeled service boundary (e.g., public APIs, partner integrations, web entry points). " +
                        "This is a quick scan for exposure, auth, and interface contracts."
                }
            }
        };

        var entryExports = GetEntryExports(plan);
        var terminalEntryExports = FilterToTerminalEntryExports(plan, entryExports);

        if (terminalEntryExports.Count == 0)
        {
            section.Blocks.Add(new ParagraphBlock
            {
                Text = "No entry points detected (no exports marked public/external and no exports with consumers)."
            });
            return section;
        }

        if (terminalEntryExports.Count < entryExports.Count)
        {
            section.Blocks.Add(new ParagraphBlock
            {
                Text =
                    $"Showing {terminalEntryExports.Count} terminal entry point(s) (excluding {entryExports.Count - terminalEntryExports.Count} exposed export(s) that are reachable from another entry point’s dependency chain)."
            });
        }

        var table = new TableBlock
        {
            Headers = { "Service", "Export", "Visibility", "Protocol", "Auth", "Consumers", "Data" }
        };

        foreach (var exp in terminalEntryExports)
        {
            var service = plan.Entities.Services.FirstOrDefault(s => s.Id == exp.ServiceRef);
            string serviceCell;
            if (service != null)
            {
                if (serviceHrefFactory != null)
                {
                    var href = serviceHrefFactory(service);
                    serviceCell = $"[{service.Description ?? service.Name}]({href})";
                }
                else
                {
                    serviceCell = $"[{service.Description ?? service.Name}](#{ToAnchorId($"service-{service.Name}")})";
                }
            }
            else
            {
                serviceCell = exp.ServiceRef;
            }

            var consumers = exp.Consumers is { Count: > 0 }
                ? string.Join(", ", exp.Consumers.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c).Take(3))
                : string.Empty;

            if (exp.Consumers is { Count: > 3 })
                consumers += (consumers.Length == 0 ? "" : ", ") + $"+{exp.Consumers.Count - 3} more";

            var data = exp.DataClassRefs is { Count: > 0 }
                ? string.Join(", ", exp.DataClassRefs.OrderBy(x => x))
                : string.Empty;

            table.Rows.Add(new TableRow
            {
                Cells =
                {
                    serviceCell,
                    exp.Name,
                    exp.Visibility ?? string.Empty,
                    exp.Protocol ?? string.Empty,
                    exp.Auth ?? string.Empty,
                    consumers,
                    data
                }
            });
        }

        section.Blocks.Add(table);
        return section;
    }

    private static ReportSection BuildHotspots(CompiledFlightPlan plan, Func<ServiceEntity, string>? serviceHrefFactory = null)
    {
        var section = new ReportSection
        {
            Heading = "Hotspots",
            Level = 1,
            Anchor = "hotspots",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section highlights interaction hotspots: services with high fan-in/fan-out, cross-zone calls, and external exposure. " +
                        "Use these to prioritize architecture reviews and decomposition work."
                }
            }
        };

        if (plan.Entities.Services.Count == 0)
        {
            section.Blocks.Add(new ParagraphBlock { Text = "No services are defined in the compiled plan." });
            return section;
        }

        var serviceById = plan.Entities.Services
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .ToDictionary(s => s.Id, s => s, StringComparer.Ordinal);

        var metrics = plan.Entities.Services
            .Select(svc =>
            {
                var inboundServiceCalls = plan.Dependencies.Count(d => d.Kind == "service-to-service" && IsServiceExportTarget(d.To, svc.Id));

                var outboundServiceCalls = 0;
                var outboundResourceUses = 0;
                var outboundCrossZone = 0;

                foreach (var dep in plan.Dependencies.Where(d => d.From == svc.Id))
                {
                    if (dep.Kind == "service-to-service")
                    {
                        outboundServiceCalls++;

                        var toServiceId = dep.To;
                        if (TrySplitExportId(dep.To, out var parsedServiceId, out _))
                            toServiceId = parsedServiceId;

                        if (serviceById.TryGetValue(svc.Id, out var fromSvc) && serviceById.TryGetValue(toServiceId, out var toSvc))
                        {
                            if (!string.IsNullOrWhiteSpace(fromSvc.ZoneRef) && !string.IsNullOrWhiteSpace(toSvc.ZoneRef) &&
                                !string.Equals(fromSvc.ZoneRef, toSvc.ZoneRef, StringComparison.OrdinalIgnoreCase))
                            {
                                outboundCrossZone++;
                            }
                        }
                    }
                    else if (dep.Kind == "service-to-resource")
                    {
                        outboundResourceUses++;
                    }
                }

                var externalExports = plan.Interfaces.Count(i =>
                    i.ServiceRef == svc.Id &&
                    (
                        string.Equals(i.Visibility, "external", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(i.Visibility, "public", StringComparison.OrdinalIgnoreCase) ||
                        (i.Consumers is { Count: > 0 })
                    ));

                var score = inboundServiceCalls + outboundServiceCalls + outboundResourceUses + (outboundCrossZone * 2) + (externalExports * 2);

                return new
                {
                    svc.Id,
                    Zone = svc.ZoneRef ?? string.Empty,
                    Inbound = inboundServiceCalls,
                    OutboundSvc = outboundServiceCalls,
                    OutboundRes = outboundResourceUses,
                    CrossZoneOut = outboundCrossZone,
                    ExternalExports = externalExports,
                    Score = score
                };
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Inbound)
            .ThenBy(x => x.Id)
            .Take(20)
            .ToList();

        section.Blocks.Add(new ParagraphBlock
        {
            Text =
                "Hotspot score is a heuristic: inbound calls + outbound calls + resource uses + (2× cross-zone outbound) + (2× externally exposed exports). " +
                "Treat this as a review queue, not a quality verdict."
        });

        var hotspotsTable = new TableBlock
        {
            Headers = { "Service", "Zone", "Inbound", "Outbound (Svc)", "Outbound (Res)", "Cross-Zone Out", "External Exports", "Score" }
        };

        foreach (var h in metrics)
        {
            var service = plan.Entities.Services.FirstOrDefault(s => s.Id == h.Id);
            string serviceCell;
            if (service != null)
            {
                if (serviceHrefFactory != null)
                {
                    var href = serviceHrefFactory(service);
                    serviceCell = $"[{service.Description ?? service.Name}]({href})";
                }
                else
                {
                    serviceCell = $"[{service.Description ?? service.Name}](#{ToAnchorId($"service-{service.Name}")})";
                }
            }
            else
            {
                serviceCell = h.Id;
            }
            
            hotspotsTable.Rows.Add(new TableRow
            {
                Cells =
                {
                    serviceCell,
                    h.Zone,
                    h.Inbound.ToString(),
                    h.OutboundSvc.ToString(),
                    h.OutboundRes.ToString(),
                    h.CrossZoneOut.ToString(),
                    h.ExternalExports.ToString(),
                    h.Score.ToString()
                }
            });
        }

        section.Blocks.Add(hotspotsTable);
        return section;
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

        var reachableFromAnyEntry = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entryServiceId in entryServiceIds)
        {
            foreach (var reachable in GetReachableServices(entryServiceId, adjacency))
            {
                reachableFromAnyEntry.Add(reachable);
            }
        }

        var terminalEntryServiceIds = entryServiceIds
            .Where(svcId => !reachableFromAnyEntry.Contains(svcId))
            .ToHashSet(StringComparer.Ordinal);

        if (terminalEntryServiceIds.Count == 0) return entryExports;

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

    private static ReportSection BuildAtAGlance(CompiledFlightPlan plan)
    {
        var kv = new KeyValueTableBlock();
        
        if (!string.IsNullOrWhiteSpace(plan.Application?.Name))
            kv.Rows.Add(kv.New("Application", plan.Application.Name));
        // Note: options not available in static method, will be refactored if needed
        
        kv.Rows.Add(kv.New("Services", plan.Entities.Services.Count.ToString()));
        kv.Rows.Add(kv.New("Resources", plan.Entities.Resources.Count.ToString()));
        kv.Rows.Add(kv.New("Interfaces", plan.Interfaces.Count.ToString()));
        kv.Rows.Add(kv.New("Dependencies", plan.Dependencies.Count.ToString()));

        return new ReportSection
        {
            Heading = "At a glance",
            Level = 1,
            Anchor = "at-a-glance",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This report is a detailed inventory. It is useful for audits, onboarding, and operational ownership mapping. " +
                        "For higher-level system structure, see the Architecture Overview report."
                },
                kv
            }
        };
    }

    private static ReportSection BuildServiceCatalog(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "Service Catalog",
            Level = 1,
            Anchor = "service-catalog",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Includes a summary table, followed by per-service details (exports and inbound/outbound dependencies)."
                }
            }
        };

        if (plan.Entities.Services.Count == 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Warning,
                Title = "No services defined",
                Message = "No services were found in the compiled plan."
            });
            return section;
        }

        var summary = new TableBlock
        {
            Headers = { "Service", "Owner", "Platform", "Zone", "Exports", "Outbound", "Inbound", "Capabilities", "Repo" }
        };

        foreach (var svc in plan.Entities.Services.OrderBy(s => s.Name))
        {
            var exportCount = plan.Interfaces.Count(i => i.ServiceRef == svc.Id);
            var outboundCount = plan.Dependencies.Count(d => d.From == svc.Id);
            var inboundCount = plan.Dependencies.Count(d => d.Kind == "service-to-service" && IsServiceExportTarget(d.To, svc.Id));

            var capabilitiesText = FormatShortList(GetCapabilities(svc), 3);

            summary.Rows.Add(new TableRow
            {
                Cells =
                {
                    $"[{svc.Description ?? svc.Name}](#{ToAnchorId($"service-{svc.Name}")})",
                    svc.OwnerRef ?? string.Empty,
                    svc.PlatformRef ?? string.Empty,
                    svc.ZoneRef ?? string.Empty,
                    exportCount.ToString(),
                    outboundCount.ToString(),
                    inboundCount.ToString(),
                    capabilitiesText,
                    FormatRepoLinkCell(svc.RepoUrl),
                }
            });
        }

        section.Blocks.Add(summary);

        foreach (var svc in plan.Entities.Services.OrderBy(s => s.Name))
            section.Children.Add(BuildServiceDetailSection(plan, svc, indexHref: "#service-catalog", level: 2));

        return section;
    }

    private static ReportSection BuildServiceCatalogIndex(CompiledFlightPlan plan, Func<ServiceEntity, string> serviceHrefFactory)
    {
        var section = new ReportSection
        {
            Heading = "Service Catalog",
            Level = 1,
            Anchor = "service-catalog",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text = "This is an index of services. Each service links to a separate detail page."
                }
            }
        };

        if (plan.Entities.Services.Count == 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Warning,
                Title = "No services defined",
                Message = "No services were found in the compiled plan."
            });
            return section;
        }

        var summary = new TableBlock
        {
            Headers = { "Service", "Repo", "Owner", "Platform", "Zone", "Exports", "Outbound", "Inbound", "Capabilities" }
        };

        foreach (var svc in plan.Entities.Services.OrderBy(s => s.Name))
        {
            var exportCount = plan.Interfaces.Count(i => i.ServiceRef == svc.Id);
            var outboundCount = plan.Dependencies.Count(d => d.From == svc.Id);
            var inboundCount = plan.Dependencies.Count(d => d.Kind == "service-to-service" && IsServiceExportTarget(d.To, svc.Id));
            var capabilitiesText = FormatShortList(GetCapabilities(svc), 3);

            var href = serviceHrefFactory(svc);
            var serviceCell = string.IsNullOrWhiteSpace(href)
                ? svc.Name
                : $"[{svc.Description ?? svc.Name}]({href})";

            summary.Rows.Add(new TableRow
            {
                Cells =
                {
                    serviceCell,
                    FormatRepoLinkCell(svc.RepoUrl),
                    svc.OwnerRef ?? string.Empty,
                    svc.PlatformRef ?? string.Empty,
                    svc.ZoneRef ?? string.Empty,
                    exportCount.ToString(),
                    outboundCount.ToString(),
                    inboundCount.ToString(),
                    capabilitiesText,
                }
            });
        }

        section.Blocks.Add(summary);
        return section;
    }

    private static ReportSection BuildServiceDetailSection(CompiledFlightPlan plan, ServiceEntity svc, string? indexHref, int level)
    {
        var svcSection = new ReportSection
        {
            Heading = $"Service: {svc.Name}",
            Level = level,
            Anchor = ToAnchorId($"service-{svc.Name}")
        };

        if (!string.IsNullOrWhiteSpace(indexHref))
        {
            svcSection.Blocks.Add(new ParagraphBlock
            {
                Text = $"Back to [Service Catalog]({indexHref})."
            });
        }

        svcSection.Blocks.Add(new ParagraphBlock
        {
            Text = $"Owner: {svc.OwnerRef ?? "(none)"} · Platform: {svc.PlatformRef ?? "(none)"} · Zone: {svc.ZoneRef ?? "(none)"}."
        });

        if (!string.IsNullOrWhiteSpace(svc.RepoUrl))
        {
            svcSection.Blocks.Add(new ParagraphBlock
            {
                Text = $"Source Repository: {FormatRepoLinkCell(svc.RepoUrl)}"
            });
        }

        // Dependency graph
        {
            var graphSection = new ReportSection
            {
                Heading = "Dependency graph",
                Level = level + 1,
                Anchor = $"{svcSection.Anchor}-dependency-graph",
                Blocks =
                {
                    new ParagraphBlock
                    {
                        Text = "Inbound and outbound dependencies for this service."
                    },
                    new CodeBlock
                    {
                        Language = "mermaid",
                        Code = BuildMermaidDependencyGraph(plan, svc)
                    }
                }
            };

            svcSection.Children.Add(graphSection);
        }

        var capabilities = GetCapabilities(svc);
        if (capabilities.Count > 0)
        {
            svcSection.Blocks.Add(new ParagraphBlock { Text = "Capabilities:" });
            svcSection.Blocks.Add(new BulletListBlock
            {
                Items = capabilities
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x)
                    .Select(x => new ListItem { Text = x })
                    .ToList()
            });
        }

        // Exports
        var exports = plan.Interfaces
            .Where(i => i.ServiceRef == svc.Id)
            .OrderBy(i => i.Name)
            .ToList();

        {
            var exportsSection = new ReportSection
            {
                Heading = $"Exports ({exports.Count})",
                Level = level + 1,
                Anchor = $"{svcSection.Anchor}-exports",
            };

            if (exports.Count == 0)
            {
                exportsSection.Blocks.Add(new ParagraphBlock { Text = "None." });
            }
            else
            {
                var exportsTable = new TableBlock
                {
                    Headers = { "Export", "Protocol", "Visibility", "Auth", "Consumers", "Classifications" }
                };

                foreach (var exp in exports)
                {
                    var consumers = exp.Consumers is { Count: > 0 }
                        ? string.Join(", ", exp.Consumers.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.Ordinal).OrderBy(c => c).Take(3))
                        : string.Empty;

                    if (exp.Consumers is { Count: > 3 })
                        consumers += (consumers.Length == 0 ? "" : ", ") + $"+{exp.Consumers.Count - 3} more";

                    var data = exp.DataClassRefs is { Count: > 0 }
                        ? string.Join(", ", exp.DataClassRefs.OrderBy(x => x))
                        : string.Empty;

                    exportsTable.Rows.Add(new TableRow
                    {
                        Cells =
                        {
                            exp.Name,
                            exp.Protocol ?? string.Empty,
                            exp.Visibility ?? string.Empty,
                            exp.Auth ?? string.Empty,
                            consumers,
                            data
                        }
                    });
                }

                exportsSection.Blocks.Add(exportsTable);
            }

            svcSection.Children.Add(exportsSection);
        }

        // Outbound dependencies
        var outbound = plan.Dependencies
            .Where(d => d.From == svc.Id)
            .OrderBy(d => d.Kind)
            .ThenBy(d => d.To)
            .ToList();

        {
            var outboundSection = new ReportSection
            {
                Heading = $"Outbound dependencies ({outbound.Count})",
                Level = level + 1,
                Anchor = $"{svcSection.Anchor}-outbound",
            };

            if (outbound.Count == 0)
            {
                outboundSection.Blocks.Add(new ParagraphBlock { Text = "None." });
            }
            else
            {
                var depsTable = new TableBlock
                {
                    Headers = { "Kind", "Target", "Export", "Access", "Details" }
                };

                foreach (var dep in outbound)
                {
                    string target;
                    string export = string.Empty;
                    string details = string.Empty;

                    if (dep.Kind == "service-to-service")
                    {
                        if (TrySplitExportId(dep.To, out var targetServiceId, out var exportName))
                        {
                            target = targetServiceId;
                            export = exportName;
                        }
                        else
                        {
                            target = dep.To;
                        }

                        var iface = plan.Interfaces.FirstOrDefault(i => i.Id == dep.To);
                        if (iface != null)
                        {
                            if (string.IsNullOrWhiteSpace(export)) export = iface.Name;
                            if (!string.IsNullOrWhiteSpace(iface.Protocol)) details += $"protocol {iface.Protocol}";
                            if (!string.IsNullOrWhiteSpace(iface.Visibility)) details += (details.Length == 0 ? "" : ", ") + iface.Visibility;
                            if (!string.IsNullOrWhiteSpace(iface.Auth)) details += (details.Length == 0 ? "" : ", ") + $"auth {iface.Auth}";
                        }
                    }
                    else if (dep.Kind == "service-to-resource")
                    {
                        var resource = plan.Entities.Resources.FirstOrDefault(r => r.Id == dep.To);
                        target = resource?.Name ?? dep.To;
                        if (!string.IsNullOrWhiteSpace(resource?.ResourceKind))
                            details = $"kind {resource!.ResourceKind}";
                    }
                    else
                    {
                        target = dep.To;
                    }

                    depsTable.Rows.Add(new TableRow
                    {
                        Cells =
                        {
                            dep.Kind,
                            target,
                            export,
                            dep.Access ?? string.Empty,
                            details
                        }
                    });
                }

                outboundSection.Blocks.Add(depsTable);
            }

            svcSection.Children.Add(outboundSection);
        }

        // Inbound dependencies
        var inbound = plan.Dependencies
            .Where(d => d.Kind == "service-to-service" && IsServiceExportTarget(d.To, svc.Id))
            .OrderBy(d => d.From)
            .ThenBy(d => d.To)
            .ToList();

        var inboundExternal = plan.Interfaces
            .Where(i => i.ServiceRef == svc.Id && i.Consumers != null && i.Consumers.Count > 0)
            .SelectMany(i => i.Consumers!.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => new { ExportId = i.Id, ExportName = i.Name, Consumer = c }))
            .OrderBy(x => x.Consumer)
            .ThenBy(x => x.ExportId)
            .ToList();

        {
            var inboundCount = inbound.Count + inboundExternal.Count;
            var inboundSection = new ReportSection
            {
                Heading = $"Inbound dependencies ({inboundCount})",
                Level = level + 1,
                Anchor = $"{svcSection.Anchor}-inbound",
            };

            if (inboundCount == 0)
            {
                inboundSection.Blocks.Add(new ParagraphBlock { Text = "None." });
            }
            else
            {
                var inboundTable = new TableBlock
                {
                    Headers = { "Caller", "Calls Export", "Access", "Details" }
                };

                foreach (var dep in inbound)
                {
                    _ = TrySplitExportId(dep.To, out _, out var exportName);

                    string details = string.Empty;
                    var iface = plan.Interfaces.FirstOrDefault(i => i.Id == dep.To);
                    if (iface != null)
                    {
                        if (!string.IsNullOrWhiteSpace(iface.Protocol)) details += $"protocol {iface.Protocol}";
                        if (!string.IsNullOrWhiteSpace(iface.Visibility)) details += (details.Length == 0 ? "" : ", ") + iface.Visibility;
                        if (!string.IsNullOrWhiteSpace(iface.Auth)) details += (details.Length == 0 ? "" : ", ") + $"auth {iface.Auth}";
                    }

                    inboundTable.Rows.Add(new TableRow
                    {
                        Cells =
                        {
                            dep.From,
                            exportName ?? string.Empty,
                            dep.Access ?? string.Empty,
                            details
                        }
                    });
                }

                foreach (var ext in inboundExternal)
                {
                    inboundTable.Rows.Add(new TableRow
                    {
                        Cells =
                        {
                            ext.Consumer,
                            ext.ExportName,
                            string.Empty,
                            "external consumer"
                        }
                    });
                }

                inboundSection.Blocks.Add(inboundTable);
            }

            svcSection.Children.Add(inboundSection);
        }

        return svcSection;
    }

    private static string BuildMermaidDependencyGraph(CompiledFlightPlan plan, ServiceEntity svc)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("flowchart LR");

        var serviceById = plan.Entities.Services
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .ToDictionary(s => s.Id!, s => s, StringComparer.Ordinal);

        var serviceIdOrName = svc.Id ?? svc.Name;
        var thisId = ToMermaidId($"svc_{serviceIdOrName}");
        sb.AppendLine($"{thisId}[\"{EscapeMermaidLabel(svc.Name)}\"]");

        var declaredNodes = new HashSet<string>(StringComparer.Ordinal) { thisId };
        var declaredEdges = new HashSet<string>(StringComparer.Ordinal);

        void EnsureNode(string nodeId, string label)
        {
            if (!declaredNodes.Add(nodeId)) return;
            sb.AppendLine($"{nodeId}[\"{EscapeMermaidLabel(label)}\"]");
        }

        void AddEdge(string fromId, string toId, string? label)
        {
            var edgeLabel = string.IsNullOrWhiteSpace(label) ? string.Empty : $"|{EscapeMermaidEdgeLabel(label)}|";
            var line = $"{fromId} -->{edgeLabel} {toId}";
            if (!declaredEdges.Add(line)) return;
            sb.AppendLine(line);
        }

        // Outbound dependencies (service-to-service + service-to-resource)
        if (!string.IsNullOrWhiteSpace(svc.Id))
        {
            foreach (var dep in plan.Dependencies.Where(d => d.From == svc.Id).OrderBy(d => d.Kind).ThenBy(d => d.To))
        {
            if (string.IsNullOrWhiteSpace(dep.Kind) || string.IsNullOrWhiteSpace(dep.To)) continue;

            if (string.Equals(dep.Kind, "service-to-service", StringComparison.Ordinal))
            {
                var toServiceId = dep.To;
                string? exportName = null;

                if (TrySplitExportId(dep.To, out var parsedServiceId, out var parsedExportName))
                {
                    toServiceId = parsedServiceId;
                    exportName = parsedExportName;
                }
                else
                {
                    var iface = plan.Interfaces.FirstOrDefault(i => i.Id == dep.To);
                    if (iface != null)
                    {
                        toServiceId = iface.ServiceRef;
                        exportName = iface.Name;
                    }
                }

                if (string.IsNullOrWhiteSpace(toServiceId)) continue;
                var toNodeId = ToMermaidId($"svc_{toServiceId}");

                var toLabel = serviceById.TryGetValue(toServiceId, out var toSvc)
                    ? toSvc.Name
                    : toServiceId;

                EnsureNode(toNodeId, toLabel);
                AddEdge(thisId, toNodeId, exportName);
            }
            else if (string.Equals(dep.Kind, "service-to-resource", StringComparison.Ordinal))
            {
                var res = plan.Entities.Resources.FirstOrDefault(r => r.Id == dep.To);
                var resId = res?.Id ?? dep.To;
                var resLabel = res?.Name ?? dep.To;
                var resKind = res?.ResourceKind;
                var resNodeId = ToMermaidId($"res_{resId}");

                EnsureNode(resNodeId, string.IsNullOrWhiteSpace(resKind) ? $"resource: {resLabel}" : $"resource: {resLabel} ({resKind})");
                AddEdge(thisId, resNodeId, "uses");
            }
        }

        }

        // Inbound dependencies (service-to-service where this service is the export target)
        if (!string.IsNullOrWhiteSpace(serviceIdOrName))
        {
            foreach (var dep in plan.Dependencies.Where(d => d.Kind == "service-to-service" && IsServiceExportTarget(d.To, serviceIdOrName)).OrderBy(d => d.From).ThenBy(d => d.To))
            {
            if (string.IsNullOrWhiteSpace(dep.From)) continue;

            _ = TrySplitExportId(dep.To, out _, out var exportName);
            if (string.IsNullOrWhiteSpace(exportName))
            {
                var iface = plan.Interfaces.FirstOrDefault(i => i.Id == dep.To);
                exportName = iface?.Name;
            }

            var fromNodeId = ToMermaidId($"svc_{dep.From}");
            var fromLabel = serviceById.TryGetValue(dep.From, out var fromSvc)
                ? fromSvc.Name
                : dep.From;

            EnsureNode(fromNodeId, fromLabel);
            AddEdge(fromNodeId, thisId, exportName);
        }

        }

        // Inbound external consumers (from interface consumers list)
        var serviceByName = plan.Entities.Services
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

        var inboundExternal = plan.Interfaces
            .Where(i => i.ServiceRef == svc.Id && i.Consumers != null && i.Consumers.Count > 0)
            .SelectMany(i => i.Consumers!.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => new { ExportId = i.Id, ExportName = i.Name, Consumer = c.Trim() }))
            .OrderBy(x => x.Consumer)
            .ThenBy(x => x.ExportId)
            .ToList();

        foreach (var ext in inboundExternal)
        {
            var consumer = ext.Consumer;
            if (string.IsNullOrWhiteSpace(consumer)) continue;

            // If the consumer looks like a known service, treat it as a service node.
            ServiceEntity? consumerSvc = null;
            var isKnownService = serviceById.ContainsKey(consumer) || serviceByName.TryGetValue(consumer, out consumerSvc);

            if (isKnownService)
            {
                var consumerId = serviceById.ContainsKey(consumer)
                    ? consumer
                    : (consumerSvc!.Id ?? consumerSvc.Name);

                var consumerLabel = serviceById.TryGetValue(consumer, out var svcById)
                    ? svcById.Name
                    : (consumerSvc!.Name ?? consumer);

                var consumerNodeId = ToMermaidId($"svc_{consumerId}");
                EnsureNode(consumerNodeId, consumerLabel);
                AddEdge(consumerNodeId, thisId, ext.ExportName);
                continue;
            }

            var extNodeId = ToMermaidId($"ext_{consumer}");
            EnsureNode(extNodeId, $"external: {consumer}");
            AddEdge(extNodeId, thisId, ext.ExportName);
        }

        return sb.ToString().TrimEnd();
    }

    private static string ToMermaidId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "n";

        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }

        var id = sb.ToString();
        if (char.IsDigit(id[0])) id = "n_" + id;
        return id;
    }

    private static string EscapeMermaidLabel(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string EscapeMermaidEdgeLabel(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return EscapeMermaidLabel(text).Replace("|", "/", StringComparison.Ordinal);
    }

    private static List<Finding> CollectFindings(CompiledFlightPlan plan)
    {
        var findings = new List<Finding>();

        var servicesMissingOwner = plan.Entities.Services
            .Where(s => string.IsNullOrWhiteSpace(s.OwnerRef))
            .Select(s => s.Name)
            .OrderBy(n => n)
            .ToList();

        if (servicesMissingOwner.Count > 0)
        {
            findings.Add(new Finding
            {
                ReportId = "service-catalog",
                SectionHeading = "Service Catalog",
                SectionAnchorId = "service-catalog",
                Severity = FindingSeverity.Medium,
                Title = "Services missing owner assignment",
                Summary = $"{servicesMissingOwner.Count} service(s) do not declare an owner/team.",
                Evidence = servicesMissingOwner.Take(10).ToList()
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
                ReportId = "service-catalog",
                SectionHeading = "Service Catalog",
                SectionAnchorId = "service-catalog",
                Severity = FindingSeverity.Low,
                Title = "Services missing repo URL",
                Summary = $"{servicesMissingRepo.Count} service(s) do not declare a repoUrl.",
                Evidence = servicesMissingRepo.Take(10).ToList()
            });
        }

        return findings;
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

    private static bool IsServiceExportTarget(string to, string serviceId)
    {
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(serviceId)) return false;

        if (TrySplitExportId(to, out var targetServiceId, out _))
        {
            return string.Equals(targetServiceId, serviceId, StringComparison.Ordinal);
        }

        return string.Equals(to, serviceId, StringComparison.Ordinal);
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

    private static string ToAnchorId(string? heading)
    {
        var text = (heading ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return "section";

        var sb = new System.Text.StringBuilder(text.Length);
        var prevDash = false;
        foreach (var ch in text)
        {
            var c = char.ToLowerInvariant(ch);
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (ok)
            {
                sb.Append(c);
                prevDash = false;
                continue;
            }

            if (!prevDash)
            {
                sb.Append('-');
                prevDash = true;
            }
        }

        return sb.ToString().Trim('-');
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

        return list;
    }

    private static bool TryGetAnnotationValue(IReadOnlyDictionary<string, object> annotations, string key, out object? value)
    {
        value = null;
        if (annotations.TryGetValue(key, out var direct))
        {
            value = direct;
            return true;
        }

        // Be forgiving about casing.
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

        var distinct = items
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x)
            .ToList();

        var shown = distinct.Take(take).ToList();
        var text = string.Join(", ", shown);
        if (distinct.Count > take)
            text += (text.Length == 0 ? string.Empty : ", ") + $"+{distinct.Count - take} more";

        return text;
    }
}
