namespace FlightPlan.Reporting;

/// <summary>
/// Produces a detailed resource catalog intended for audits, onboarding, and infrastructure inventory.
/// Focuses on resources, consumers, dependencies, and traceability (owners/locations).
/// </summary>
public sealed class ResourceCatalogReportGenerator : IReportGenerator
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
            Title = "Resource Catalog",
            Subtitle = "Detailed inventory of resources, consumers, and dependencies",
            Metadata = new ReportMetadata
            {
                ApplicationName = plan.Application?.Name,
                Version = plan.Metadata.Format,
                Tags =
                {
                    ["report"] = "resource-catalog",
                    ["generated-by"] = "flightplan"
                }
            }
        };

        doc.Sections.Add(BuildAtAGlance(plan));
        doc.Sections.Add(BuildResourceCatalog(plan));
        doc.Sections.Add(BuildCriticalResources(plan));
        doc.Sections.Add(BuildHotspots(plan));

        doc.Findings.AddRange(CollectFindings(plan));

        return doc;
    }

    public ReportDocument GenerateIndex(CompiledFlightPlan plan, Func<ResourceEntity, string> resourceHrefFactory)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (resourceHrefFactory == null) throw new ArgumentNullException(nameof(resourceHrefFactory));

        var doc = new ReportDocument
        {
            Title = "Resource Catalog",
            Subtitle = "Resource index (one file per resource)",
            Metadata = new ReportMetadata
            {
                ApplicationName = plan.Application?.Name,
                Version = plan.Metadata.Format,
                Tags =
                {
                    ["report"] = "resource-catalog",
                    ["generated-by"] = "flightplan"
                }
            }
        };

        doc.Sections.Add(BuildAtAGlance(plan));
        doc.Sections.Add(BuildResourceCatalogIndex(plan, resourceHrefFactory));
        doc.Sections.Add(BuildCriticalResources(plan));
        doc.Sections.Add(BuildHotspots(plan));
        doc.Findings.AddRange(CollectFindings(plan));

        return doc;
    }

    public ReportDocument GenerateResourceDetail(CompiledFlightPlan plan, ResourceEntity resource, string? indexHref)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (resource == null) throw new ArgumentNullException(nameof(resource));

        var doc = new ReportDocument
        {
            Title = $"Resource: {resource.Name}",
            Subtitle = "Resource catalog detail",
            Metadata = new ReportMetadata
            {
                ApplicationName = plan.Application?.Name,
                Version = plan.Metadata.Format,
                Tags =
                {
                    ["report"] = "resource-catalog",
                    ["generated-by"] = "flightplan",
                    ["resource"] = resource.Id ?? resource.Name
                }
            }
        };

        var section = BuildResourceDetailSection(plan, resource, indexHref: indexHref, level: 1);
        doc.Sections.Add(section);

        return doc;
    }

    private static ReportSection BuildCriticalResources(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "Critical Resources",
            Level = 1,
            Anchor = "critical-resources",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Critical resources are those with high fan-in (many services depend on them), cross-zone access, or external exposure. " +
                        "These warrant special attention for security, availability, and disaster recovery planning."
                }
            }
        };

        var criticalResources = plan.Entities.Resources
            .Select(res =>
            {
                var inboundCount = plan.Dependencies.Count(d => d.Kind == "service-to-resource" && d.To == res.Id);
                
                var consumers = plan.Dependencies
                    .Where(d => d.Kind == "service-to-resource" && d.To == res.Id)
                    .Select(d => d.From)
                    .Distinct()
                    .ToList();

                var crossZoneConsumers = 0;
                var serviceById = plan.Entities.Services
                    .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                    .ToDictionary(s => s.Id, s => s, StringComparer.Ordinal);

                foreach (var consumerId in consumers)
                {
                    if (serviceById.TryGetValue(consumerId, out var consumer))
                    {
                        if (!string.IsNullOrWhiteSpace(res.ZoneRef) && !string.IsNullOrWhiteSpace(consumer.ZoneRef) &&
                            !string.Equals(res.ZoneRef, consumer.ZoneRef, StringComparison.OrdinalIgnoreCase))
                        {
                            crossZoneConsumers++;
                        }
                    }
                }

                var hasExternalAccess = HasExternalAccess(res);
                var score = inboundCount + (crossZoneConsumers * 2) + (hasExternalAccess ? 5 : 0);

                return new
                {
                    res.Id,
                    res.Name,
                    ResourceKind = res.ResourceKind ?? string.Empty,
                    Zone = res.ZoneRef ?? string.Empty,
                    Consumers = inboundCount,
                    CrossZone = crossZoneConsumers,
                    External = hasExternalAccess,
                    Score = score
                };
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Consumers)
            .ThenBy(x => x.Name)
            .Take(20)
            .ToList();

        if (criticalResources.Count == 0)
        {
            section.Blocks.Add(new ParagraphBlock { Text = "No critical resources detected based on usage patterns." });
            return section;
        }

        section.Blocks.Add(new ParagraphBlock
        {
            Text =
                "Criticality score is a heuristic: consumers + (2× cross-zone consumers) + (5× external access). " +
                "Treat this as a review queue for security and reliability planning."
        });

        var table = new TableBlock
        {
            Headers = { "Resource", "Kind", "Zone", "Consumers", "Cross-Zone", "External", "Score" }
        };

        foreach (var r in criticalResources)
        {
            table.Rows.Add(new TableRow
            {
                Cells =
                {
                    r.Name,
                    r.ResourceKind,
                    r.Zone,
                    r.Consumers.ToString(),
                    r.CrossZone.ToString(),
                    r.External ? "Yes" : "No",
                    r.Score.ToString()
                }
            });
        }

        section.Blocks.Add(table);
        return section;
    }

    private static ReportSection BuildHotspots(CompiledFlightPlan plan)
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
                        "Resource hotspots are those with the highest number of service dependencies. " +
                        "These resources may benefit from optimization, caching, or architectural review."
                }
            }
        };

        if (plan.Entities.Resources.Count == 0)
        {
            section.Blocks.Add(new ParagraphBlock { Text = "No resources are defined in the compiled plan." });
            return section;
        }

        var metrics = plan.Entities.Resources
            .Select(res =>
            {
                var inboundCount = plan.Dependencies.Count(d => d.Kind == "service-to-resource" && d.To == res.Id);
                
                var consumers = plan.Dependencies
                    .Where(d => d.Kind == "service-to-resource" && d.To == res.Id)
                    .Select(d => d.From)
                    .Distinct()
                    .ToList();

                return new
                {
                    res.Id,
                    res.Name,
                    ResourceKind = res.ResourceKind ?? string.Empty,
                    Zone = res.ZoneRef ?? string.Empty,
                    Consumers = inboundCount,
                    UniqueConsumers = consumers.Count
                };
            })
            .OrderByDescending(x => x.Consumers)
            .ThenByDescending(x => x.UniqueConsumers)
            .ThenBy(x => x.Name)
            .Take(20)
            .ToList();

        var hotspotsTable = new TableBlock
        {
            Headers = { "Resource", "Kind", "Zone", "Total Uses", "Unique Consumers" }
        };

        foreach (var h in metrics)
        {
            hotspotsTable.Rows.Add(new TableRow
            {
                Cells =
                {
                    h.Name,
                    h.ResourceKind,
                    h.Zone,
                    h.Consumers.ToString(),
                    h.UniqueConsumers.ToString()
                }
            });
        }

        section.Blocks.Add(hotspotsTable);
        return section;
    }

    private static ReportSection BuildAtAGlance(CompiledFlightPlan plan)
    {
        var kv = new KeyValueTableBlock();
        
        if (!string.IsNullOrWhiteSpace(plan.Application?.Name))
            kv.Rows.Add(kv.New("Application", plan.Application.Name));
        // Note: options not available in static method, will be refactored if needed
        
        kv.Rows.Add(kv.New("Resources", plan.Entities.Resources.Count.ToString()));
        kv.Rows.Add(kv.New("Services", plan.Entities.Services.Count.ToString()));
        kv.Rows.Add(kv.New("Resource Dependencies", 
            plan.Dependencies.Count(d => d.Kind == "service-to-resource").ToString()));

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
                        "This report is a detailed inventory of infrastructure resources. It is useful for audits, capacity planning, and operational ownership mapping. " +
                        "For service dependencies, see the Service Catalog report."
                },
                kv
            }
        };
    }

    private static ReportSection BuildResourceCatalog(CompiledFlightPlan plan)
    {
        var section = new ReportSection
        {
            Heading = "Resource Catalog",
            Level = 1,
            Anchor = "resource-catalog",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Includes a summary table, followed by per-resource details (kind, consumers, and configuration)."
                }
            }
        };

        if (plan.Entities.Resources.Count == 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Warning,
                Title = "No resources defined",
                Message = "No resources were found in the compiled plan."
            });
            return section;
        }

        var summary = new TableBlock
        {
            Headers = { "Resource", "Kind", "Owner", "Platform", "Zone", "Consumers", "External Access" }
        };

        foreach (var res in plan.Entities.Resources.OrderBy(r => r.Name))
        {
            var consumerCount = plan.Dependencies.Count(d => d.Kind == "service-to-resource" && d.To == res.Id);
            var hasExternal = HasExternalAccess(res);

            summary.Rows.Add(new TableRow
            {
                Cells =
                {
                    $"[{res.Name}](#{ToAnchorId($"resource-{res.Name}")})",
                    res.ResourceKind ?? string.Empty,
                    res.OwnerRef ?? string.Empty,
                    res.PlatformRef ?? string.Empty,
                    res.ZoneRef ?? string.Empty,
                    consumerCount.ToString(),
                    hasExternal ? "Yes" : "No"
                }
            });
        }

        section.Blocks.Add(summary);

        foreach (var res in plan.Entities.Resources.OrderBy(r => r.Name))
            section.Children.Add(BuildResourceDetailSection(plan, res, indexHref: "#resource-catalog", level: 2));

        return section;
    }

    private static ReportSection BuildResourceCatalogIndex(CompiledFlightPlan plan, Func<ResourceEntity, string> resourceHrefFactory)
    {
        var section = new ReportSection
        {
            Heading = "Resource Catalog",
            Level = 1,
            Anchor = "resource-catalog",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text = "This is an index of resources. Each resource links to a separate detail page."
                }
            }
        };

        if (plan.Entities.Resources.Count == 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Warning,
                Title = "No resources defined",
                Message = "No resources were found in the compiled plan."
            });
            return section;
        }

        var summary = new TableBlock
        {
            Headers = { "Resource", "Kind", "Owner", "Platform", "Zone", "Consumers", "External Access" }
        };

        foreach (var res in plan.Entities.Resources.OrderBy(r => r.Name))
        {
            var consumerCount = plan.Dependencies.Count(d => d.Kind == "service-to-resource" && d.To == res.Id);
            var hasExternal = HasExternalAccess(res);

            var href = resourceHrefFactory(res);
            var resourceCell = string.IsNullOrWhiteSpace(href)
                ? res.Name
                : $"[{res.Name}]({href})";

            summary.Rows.Add(new TableRow
            {
                Cells =
                {
                    resourceCell,
                    res.ResourceKind ?? string.Empty,
                    res.OwnerRef ?? string.Empty,
                    res.PlatformRef ?? string.Empty,
                    res.ZoneRef ?? string.Empty,
                    consumerCount.ToString(),
                    hasExternal ? "Yes" : "No"
                }
            });
        }

        section.Blocks.Add(summary);
        return section;
    }

    private static ReportSection BuildResourceDetailSection(CompiledFlightPlan plan, ResourceEntity res, string? indexHref, int level)
    {
        var resSection = new ReportSection
        {
            Heading = $"Resource: {res.Name}",
            Level = level,
            Anchor = ToAnchorId($"resource-{res.Name}")
        };

        if (!string.IsNullOrWhiteSpace(indexHref))
        {
            resSection.Blocks.Add(new ParagraphBlock
            {
                Text = $"Back to [Resource Catalog]({indexHref})."
            });
        }

        resSection.Blocks.Add(new ParagraphBlock
        {
            Text = $"Kind: {res.ResourceKind ?? "(none)"} · Owner: {res.OwnerRef ?? "(none)"} · Platform: {res.PlatformRef ?? "(none)"} · Zone: {res.ZoneRef ?? "(none)"}."
        });

        // Dependency graph
        {
            var graphSection = new ReportSection
            {
                Heading = "Dependency graph",
                Level = level + 1,
                Anchor = $"{resSection.Anchor}-dependency-graph",
                Blocks =
                {
                    new ParagraphBlock
                    {
                        Text = "Services that depend on this resource."
                    },
                    new CodeBlock
                    {
                        Language = "mermaid",
                        Code = BuildMermaidDependencyGraph(plan, res)
                    }
                }
            };

            resSection.Children.Add(graphSection);
        }

        // Configuration
        if (res.Annotations != null && res.Annotations.Count > 0)
        {
            var configSection = new ReportSection
            {
                Heading = "Configuration",
                Level = level + 1,
                Anchor = $"{resSection.Anchor}-configuration"
            };

            var configTable = new TableBlock
            {
                Headers = { "Property", "Value" }
            };

            foreach (var kvp in res.Annotations.OrderBy(k => k.Key))
            {
                var value = kvp.Value?.ToString() ?? string.Empty;
                if (value.Length > 100)
                    value = value.Substring(0, 97) + "...";

                configTable.Rows.Add(new TableRow
                {
                    Cells = { kvp.Key, value }
                });
            }

            configSection.Blocks.Add(configTable);
            resSection.Children.Add(configSection);
        }

        // Consumers (services that use this resource)
        var consumers = plan.Dependencies
            .Where(d => d.Kind == "service-to-resource" && d.To == res.Id)
            .OrderBy(d => d.From)
            .ToList();

        {
            var consumersSection = new ReportSection
            {
                Heading = $"Consumers ({consumers.Count})",
                Level = level + 1,
                Anchor = $"{resSection.Anchor}-consumers",
            };

            if (consumers.Count == 0)
            {
                consumersSection.Blocks.Add(new ParagraphBlock { Text = "None." });
            }
            else
            {
                var consumersTable = new TableBlock
                {
                    Headers = { "Service", "Access", "Details" }
                };

                var serviceById = plan.Entities.Services
                    .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                    .ToDictionary(s => s.Id, s => s, StringComparer.Ordinal);

                foreach (var dep in consumers)
                {
                    var serviceName = dep.From;
                    var details = string.Empty;

                    if (serviceById.TryGetValue(dep.From, out var svc))
                    {
                        serviceName = svc.Name;
                        
                        // Check for cross-zone access
                        if (!string.IsNullOrWhiteSpace(res.ZoneRef) && !string.IsNullOrWhiteSpace(svc.ZoneRef) &&
                            !string.Equals(res.ZoneRef, svc.ZoneRef, StringComparison.OrdinalIgnoreCase))
                        {
                            details = $"cross-zone (service in {svc.ZoneRef}, resource in {res.ZoneRef})";
                        }
                    }

                    consumersTable.Rows.Add(new TableRow
                    {
                        Cells =
                        {
                            serviceName,
                            dep.Access ?? string.Empty,
                            details
                        }
                    });
                }

                consumersSection.Blocks.Add(consumersTable);
            }

            resSection.Children.Add(consumersSection);
        }

        return resSection;
    }

    private static string BuildMermaidDependencyGraph(CompiledFlightPlan plan, ResourceEntity res)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("flowchart LR");

        var resourceIdOrName = res.Id ?? res.Name;
        var thisId = ToMermaidId($"res_{resourceIdOrName}");
        sb.AppendLine($"{thisId}[\"{EscapeMermaidLabel(res.Name)}<br/>{EscapeMermaidLabel(res.ResourceKind ?? "resource")}\"]");

        var declaredNodes = new HashSet<string>(StringComparer.Ordinal) { thisId };

        void EnsureNode(string nodeId, string label)
        {
            if (!declaredNodes.Add(nodeId)) return;
            sb.AppendLine($"{nodeId}[\"{EscapeMermaidLabel(label)}\"]");
        }

        var serviceById = plan.Entities.Services
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .ToDictionary(s => s.Id, s => s, StringComparer.Ordinal);

        // Consumers (services using this resource)
        var consumers = plan.Dependencies
            .Where(d => d.Kind == "service-to-resource" && d.To == res.Id)
            .OrderBy(d => d.From)
            .ToList();

        foreach (var dep in consumers)
        {
            if (string.IsNullOrWhiteSpace(dep.From)) continue;

            var fromNodeId = ToMermaidId($"svc_{dep.From}");
            var fromLabel = serviceById.TryGetValue(dep.From, out var fromSvc)
                ? fromSvc.Name
                : dep.From;

            EnsureNode(fromNodeId, fromLabel);
            
            var edgeLabel = string.IsNullOrWhiteSpace(dep.Access) ? "uses" : dep.Access;
            sb.AppendLine($"{fromNodeId} -->|{EscapeMermaidEdgeLabel(edgeLabel)}| {thisId}");
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

        var resourcesMissingOwner = plan.Entities.Resources
            .Where(r => string.IsNullOrWhiteSpace(r.OwnerRef))
            .Select(r => r.Name)
            .OrderBy(n => n)
            .ToList();

        if (resourcesMissingOwner.Count > 0)
        {
            findings.Add(new Finding
            {
                ReportId = "resource-catalog",
                SectionHeading = "Resource Catalog",
                SectionAnchorId = "resource-catalog",
                Severity = FindingSeverity.Medium,
                Title = "Resources missing owner assignment",
                Summary = $"{resourcesMissingOwner.Count} resource(s) do not declare an owner/team.",
                Evidence = resourcesMissingOwner.Take(10).ToList()
            });
        }

        var resourcesMissingKind = plan.Entities.Resources
            .Where(r => string.IsNullOrWhiteSpace(r.ResourceKind))
            .Select(r => r.Name)
            .OrderBy(n => n)
            .ToList();

        if (resourcesMissingKind.Count > 0)
        {
            findings.Add(new Finding
            {
                ReportId = "resource-catalog",
                SectionHeading = "Resource Catalog",
                SectionAnchorId = "resource-catalog",
                Severity = FindingSeverity.Low,
                Title = "Resources missing kind classification",
                Summary = $"{resourcesMissingKind.Count} resource(s) do not declare a resourceKind.",
                Evidence = resourcesMissingKind.Take(10).ToList()
            });
        }

        var unusedResources = plan.Entities.Resources
            .Where(r => !plan.Dependencies.Any(d => d.Kind == "service-to-resource" && d.To == r.Id))
            .Select(r => r.Name)
            .OrderBy(n => n)
            .ToList();

        if (unusedResources.Count > 0)
        {
            findings.Add(new Finding
            {
                ReportId = "resource-catalog",
                SectionHeading = "Resource Catalog",
                SectionAnchorId = "resource-catalog",
                Severity = FindingSeverity.Low,
                Title = "Unused resources detected",
                Summary = $"{unusedResources.Count} resource(s) have no service dependencies.",
                Evidence = unusedResources.Take(10).ToList()
            });
        }

        return findings;
    }

    private static bool HasExternalAccess(ResourceEntity resource)
    {
        if (resource.Annotations == null) return false;

        // Check common patterns for external access
        if (resource.Annotations.TryGetValue("external", out var external) && 
            external is bool externalBool && externalBool)
        {
            return true;
        }

        if (resource.Annotations.TryGetValue("visibility", out var visibility) &&
            visibility is string visStr &&
            (string.Equals(visStr, "external", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(visStr, "public", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
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
}
