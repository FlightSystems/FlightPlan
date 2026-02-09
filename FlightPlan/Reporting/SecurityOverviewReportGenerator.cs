namespace FlightPlan.Reporting;

/// <summary>
/// Generates a Security & Data Flow Overview report from a compiled Flight Plan.
/// Audience: Security reviewers, auditors, engineering leadership.
/// </summary>
public sealed class SecurityOverviewReportGenerator : IReportGenerator 
{
    private ReportGenerationOptions _options = new();
    
    public ReportDocument Generate(CompiledFlightPlan plan)
    {
        return Generate(plan, new ReportGenerationOptions());
    }
    
    public ReportDocument Generate(CompiledFlightPlan plan, ReportGenerationOptions options)
    {
        _options = options ?? new ReportGenerationOptions();
        var doc = new ReportDocument
        {
            Title = "Security & Data Flow Overview",
            Subtitle = plan.Application.Name,
            GeneratedAtUtc = DateTime.UtcNow,

            Metadata = new ReportMetadata
            {
                ApplicationName = plan.Application.Name,
                Version = plan.Metadata.Format,
                Tags =
                {
                    ["report"] = "security-overview",
                    ["generated-by"] = "flightplan"
                }
            },

            Sections =
            {
                BuildIntendedUse(),
                BuildExecutiveSummary(plan),
                BuildExternalExposure(plan),
                BuildDataClassificationInMotion(plan),
                BuildDataClassificationAtRest(plan),
                BuildTrustBoundaryAnalysis(plan),
                BuildInterfaceContractCompliance(plan),
                BuildThirdPartyDependencies(plan),
                BuildOwnershipSummary(plan)
            }
        };

        doc.Findings.AddRange(CollectFindings(plan));

        return doc;
    }

    private static string FormatServiceCell(ServiceEntity? service, string? fallbackServiceId = null)
    {
        if (service != null)
        {
            return CatalogLinkHelper.ServiceLink(service);
        }

        return fallbackServiceId ?? string.Empty;
    }

    private static List<Finding> CollectFindings(CompiledFlightPlan plan)
    {
        var findings = new List<Finding>();

        var externallyExposed = plan.Interfaces.Where(IsExternallyExposed).ToList();
        if (externallyExposed.Count > 0)
        {
            var evidence = externallyExposed
                .Select(i => $"{i.ServiceRef}/{i.Name} (visibility {i.Visibility ?? "unknown"}, protocol {i.Protocol ?? "unknown"}, auth {i.Auth ?? "unspecified"})")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x)
                .Take(10)
                .ToList();

            findings.Add(new Finding
            {
                ReportId = "security-overview",
                SectionHeading = "External Exposure",
                SectionAnchorId = "external-exposure",
                Severity = FindingSeverity.Medium,
                Title = "Externally exposed interfaces",
                Summary = $"{externallyExposed.Count} export(s) are marked as externally exposed. Review authentication, authorization, and rate limiting.",
                Evidence = evidence
            });
        }

        var thirdPartyClassified = plan.Entities.Resources
            .Select(r => new { Resource = r, Platform = plan.GetPlatformById(r.PlatformRef ?? string.Empty) })
            .Where(x => x.Platform?.Category?.Equals("saas", StringComparison.OrdinalIgnoreCase) == true)
            .Where(x => x.Resource.DataClassRefs is { Count: > 0 })
            .Select(x => $"{x.Resource.Name} (classifications: {string.Join(", ", x.Resource.DataClassRefs!)})")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x)
            .ToList();

        if (thirdPartyClassified.Count > 0)
        {
            findings.Add(new Finding
            {
                ReportId = "security-overview",
                SectionHeading = "Third-Party Dependencies",
                SectionAnchorId = "third-party-dependencies",
                Severity = FindingSeverity.High,
                Title = "Classified data sent to third-party systems",
                Summary = $"{thirdPartyClassified.Count} third-party resource(s) are marked as handling classified data. Validate vendor risk, DPAs, and data minimization.",
                Evidence = thirdPartyClassified.Take(10).ToList()
            });
        }

        var trustBoundaryCrossings = plan.Dependencies
            .Select(d =>
            {
                var from = plan.Resolve(d, Direction.From);
                var to = plan.Resolve(d, Direction.To);
                if (from == null || to == null)
                    return null;

                var fromZone = (from as dynamic)?.ZoneRef;
                var toZone = (to as dynamic)?.ZoneRef;
                if (fromZone == toZone)
                    return null;

                return new
                {
                    From = from.Name,
                    FromZone = fromZone ?? "unknown",
                    To = to.Name,
                    ToZone = toZone ?? "unknown",
                    Kind = d.Kind
                };
            })
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();

        if (trustBoundaryCrossings.Count > 0)
        {
            var evidence = trustBoundaryCrossings
                .Select(x => $"{x.FromZone} → {x.ToZone}: {x.From} → {x.To} ({x.Kind})")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x)
                .Take(10)
                .ToList();

            findings.Add(new Finding
            {
                ReportId = "security-overview",
                SectionHeading = "Trust Boundary Analysis",
                SectionAnchorId = "trust-boundary-analysis",
                Severity = FindingSeverity.Medium,
                Title = "Trust boundary crossings",
                Summary = $"{trustBoundaryCrossings.Count} interaction(s) cross zone boundaries. Review identity propagation, encryption in transit, and authorization between zones.",
                Evidence = evidence
            });
        }

        // Documented exceptions/expected crossings (still worth review).
        var documentedExceptionCrossings = plan.Dependencies
            .Where(d => (d.Access ?? string.Empty).Contains("expected", StringComparison.OrdinalIgnoreCase)
                        || (d.Access ?? string.Empty).Contains("exception", StringComparison.OrdinalIgnoreCase))
            .Select(d =>
            {
                var from = plan.Resolve(d, Direction.From);
                var to = plan.Resolve(d, Direction.To);
                if (from == null || to == null)
                    return null;

                var fromZone = (from as dynamic)?.ZoneRef ?? "unknown";
                var toZone = (to as dynamic)?.ZoneRef ?? "unknown";
                if (fromZone == toZone)
                    return null;

                return $"{fromZone} → {toZone}: {from.Name} → {to.Name} ({d.Kind}, access {d.Access})";
            })
            .Where(x => x != null)
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x)
            .ToList();

        if (documentedExceptionCrossings.Count > 0)
        {
            findings.Add(new Finding
            {
                ReportId = "security-overview",
                SectionHeading = "Trust Boundary Analysis",
                SectionAnchorId = "trust-boundary-analysis",
                Severity = FindingSeverity.Low,
                Title = "Documented trust boundary exceptions",
                Summary = $"{documentedExceptionCrossings.Count} cross-zone dependency(ies) are marked as access: expected/exception. Confirm compensating controls and periodically revalidate.",
                Evidence = documentedExceptionCrossings.Take(10).ToList()
            });
        }

        var unowned = plan.Entities.Services
            .Where(s => string.IsNullOrWhiteSpace(s.OwnerRef))
            .Select(s => s.Name)
            .OrderBy(n => n)
            .ToList();

        if (unowned.Count > 0)
        {
            findings.Add(new Finding
            {
                ReportId = "security-overview",
                SectionHeading = "Ownership Summary",
                SectionAnchorId = "ownership-summary",
                Severity = FindingSeverity.Medium,
                Title = "Unowned services",
                Summary = $"{unowned.Count} service(s) have no owner. Assign ownership for accountability and incident response.",
                Evidence = unowned.Take(10).ToList()
            });
        }

        // Interface & contract compliance issues
        var exportById = plan.Interfaces.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var contractIssues = new List<string>();

        foreach (var dep in plan.Dependencies)
        {
            if (dep.Kind == "service-to-service")
            {
                var targetSvcId = SplitServiceId(dep.To);
                if (targetSvcId is null)
                {
                    contractIssues.Add($"{dep.From} → {dep.To}: Invalid service dependency target");
                    continue;
                }

                if (!exportById.TryGetValue(dep.To, out var export))
                {
                    contractIssues.Add($"{dep.From} → {dep.To}: Target export not declared");
                }
                else if (!string.Equals(export.ServiceRef, targetSvcId, StringComparison.Ordinal))
                {
                    contractIssues.Add($"{dep.From} → {dep.To}: Export belongs to '{export.ServiceRef}', not '{targetSvcId}'");
                }

                if (!plan.Entities.Services.Any(s => s.Id == targetSvcId))
                {
                    contractIssues.Add($"{dep.From} → {targetSvcId}: Missing upstream service");
                }
            }
            else if (dep.Kind == "service-to-resource")
            {
                if (!plan.Entities.Resources.Any(r => r.Id == dep.To))
                {
                    contractIssues.Add($"{dep.From} → {dep.To}: Missing resource");
                }
            }
        }

        if (contractIssues.Count > 0)
        {
            findings.Add(new Finding
            {
                ReportId = "security-overview",
                SectionHeading = "Interface & Contract Compliance",
                SectionAnchorId = "interface-contract-compliance",
                Severity = FindingSeverity.High,
                Title = "Interface & contract compliance issues",
                Summary = $"{contractIssues.Count} dependency declaration(s) have contract or interface mismatches in the Flight Plan.",
                Evidence = contractIssues.Take(10).ToList()
            });
        }

        return findings;
    }

    private static ReportSection BuildIntendedUse()
    {
        return new ReportSection
        {
            Heading = "Intended Use",
            Level = 1,
            Blocks =
            {
                new CalloutBlock
                {
                    CalloutType = CalloutKind.Info,
                    Title = "Who should read this report?",
                    Message = "This report is intended for security review, compliance assessment, and architectural risk analysis."
                }
            }
        };
    }

    // ============================================================
    // EXECUTIVE SUMMARY
    // ============================================================

    private static ReportSection BuildExecutiveSummary(CompiledFlightPlan plan)
    {
        var externallyExposedServices =
            plan.Interfaces
                .Where(i => IsExternallyExposed(i))
                .Select(i => i.ServiceRef)
                .Distinct()
                .Count();

        var resourcesHandlingClassifiedData = plan.Entities.Resources.Count(r => r.DataClassRefs?.Any() == true);

        var thirdPartyResources =
            plan.Entities.Resources
                .Count(r => plan.GetPlatformById(r.PlatformRef ?? string.Empty)?.Category?.Equals("saas", StringComparison.OrdinalIgnoreCase) == true);

        var thirdPartyPlatforms =
            plan.Entities.Resources
                .Select(r => plan.GetPlatformById(r.PlatformRef ?? string.Empty))
                .Where(p => p?.Category?.Equals("saas", StringComparison.OrdinalIgnoreCase) == true)
                .Select(p => p!.Id)
                .Distinct()
                .Count();

        return new ReportSection
        {
            Heading = "Executive Summary",
            Level = 1,
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This report provides a design-time overview of the system’s security posture, " +
                        "data handling, and trust boundaries. It is intended to support security review, " +
                        "compliance assessment, and architectural risk evaluation."
                },

                new ParagraphBlock
                {
                    Text = plan.DeliveryModel?.PrimaryUsers is { Count: > 0 }
                        ? $"Declared primary users (deliveryModel.primaryUsers): {string.Join(", ", plan.DeliveryModel.PrimaryUsers.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Distinct(StringComparer.Ordinal).OrderBy(u => u))}. " +
                          "For concrete entry-point mapping, these should also appear as external export consumers (exports.*.consumers) where applicable."
                        : "Declared primary users: none (deliveryModel.primaryUsers not specified)."
                },

                new ParagraphBlock
                {
                    Text =
                        "How to read: services are compute units, resources are shared infrastructure/external systems, and exports are named interfaces exposed by services. " +
                        "Dependencies describe how services call exports or use resources."
                },

                new ParagraphBlock
                {
                    Text =
                        "Dependency kinds: service-to-service = a service calls a specific export on another service; " +
                        "service-to-resource = a service uses a shared resource (database, queue, cache, external API, etc)."
                },

                new BulletListBlock
                {
                    Items =
                    {
                        new ListItem { Text = $"Services defined: {plan.Entities.Services.Count}" },
                        new ListItem { Text = $"Externally exposed services: {externallyExposedServices}" },
                        new ListItem { Text = $"Resources handling classified data: {resourcesHandlingClassifiedData}" },
                        new ListItem { Text = $"Third-party resources: {thirdPartyResources} (across {thirdPartyPlatforms} platform(s))" }
                    }
                }
            }
        };
    }

    // ============================================================
    // EXTERNAL EXPOSURE
    // ============================================================

    private static ReportSection BuildExternalExposure(CompiledFlightPlan plan)
    {
        var rows =
            plan.Interfaces
                .OrderByDescending(IsExternallyExposed)
                .ThenBy(i => i.ServiceRef)
                .ThenBy(i => i.Name)
                .Select(i =>
                {
                    var service = plan.Entities.Services.FirstOrDefault(s => s.Id == i.ServiceRef);
                    var classifications = i.DataClassRefs != null && i.DataClassRefs.Any()
                        ? string.Join(", ", i.DataClassRefs)
                        : "none";
                    var classificationCount = i.DataClassRefs?.Count ?? 0;

                    return new TableRow
                    {
                        Cells =
                        {
                            FormatServiceCell(service, i.ServiceRef),
                            i.Name,
                            i.Visibility ?? "(unspecified)",
                            i.Protocol ?? "(unspecified)",
                            i.Auth ?? "none",
                            classificationCount.ToString(),
                            classifications
                        }
                    };
                })
                .ToList();

        return new ReportSection
        {
            Heading = "Interfaces & Exposure",
            Level = 1,
            Anchor = "external-exposure",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section lists service exports and their security-relevant properties. " +
                        "Interfaces marked as public/external should be treated as primary ingress points into the system and reviewed for appropriate authentication and access controls."
                },

                new TableBlock
                {
                    Headers =
                    {
                        "Service",
                        "Interface",
                        "Visibility",
                        "Protocol",
                        "Authentication",
                        "Classification Count",
                        "Classifications"
                    },
                    Rows = rows
                }
            }
        };
    }

    // ============================================================
    // DATA CLASSIFICATION - IN MOTION
    // ============================================================

    private static ReportSection BuildDataClassificationInMotion(CompiledFlightPlan plan)
    {
        var exportByService = plan.Interfaces
            .GroupBy(i => i.ServiceRef, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var rows = new List<TableRow>();

        foreach (var svc in plan.Entities.Services.OrderBy(s => s.Name))
        {
            if (!exportByService.TryGetValue(svc.Id, out var exports))
                continue;

            var classifiedExports = exports.Where(e => e.DataClassRefs is { Count: > 0 }).ToList();
            if (classifiedExports.Count == 0)
                continue;

            foreach (var exp in classifiedExports.OrderBy(e => e.Name))
            {
                rows.Add(new TableRow
                {
                    Cells =
                    {
                        FormatServiceCell(svc),
                        exp.Name,
                        exp.Visibility ?? "internal",
                        svc.ZoneRef ?? "unknown",
                        string.Join(", ", exp.DataClassRefs!)
                    }
                });
            }
        }

        var section = new ReportSection
        {
            Heading = "Data Classifications: In Motion",
            Level = 1,
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section identifies classified or regulated data exposed through service interfaces. " +
                        "Data 'in motion' flows through APIs, message queues, and other inter-service communication channels. " +
                        "Externally visible exports should have encryption in transit and appropriate authentication."
                }
            }
        };

        if (rows.Count > 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Info,
                Title = "Classified Data in Transit",
                Message = $"{rows.Count} service export(s) handle classified data. Verify encryption in transit and access controls."
            });

            section.Blocks.Add(new TableBlock
            {
                Headers =
                {
                    "Service",
                    "Export/Interface",
                    "Visibility",
                    "Zone",
                    "Classifications"
                },
                Rows = rows
            });
        }
        else
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Success,
                Title = "No Classified Data in Transit",
                Message = "No classified data is exposed through service exports."
            });
        }

        return section;
    }

    // ============================================================
    // DATA CLASSIFICATION - AT REST
    // ============================================================

    private static ReportSection BuildDataClassificationAtRest(CompiledFlightPlan plan)
    {
        var depsByService = plan.Dependencies
            .Where(d => d.Kind == "service-to-resource")
            .GroupBy(d => d.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var resById = plan.Entities.Resources.ToDictionary(r => r.Id, StringComparer.Ordinal);

        var rows = plan.Entities.Resources
            .Where(r => r.DataClassRefs is { Count: > 0 })
            .OrderBy(r => r.Name)
            .Select(r =>
            {
                var usedByServices = plan.Dependencies
                    .Where(d => d.Kind == "service-to-resource" && d.To == r.Id)
                    .Select(d => plan.Entities.Services.FirstOrDefault(s => s.Id == d.From)?.Name ?? d.From)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x)
                    .Take(5)
                    .ToList();

                var usedByText = usedByServices.Count > 0
                    ? string.Join(", ", usedByServices)
                    : "(none)";

                return new TableRow
                {
                    Cells =
                    {
                        r.Name,
                        r.ResourceKind,
                        r.ZoneRef ?? "unknown",
                        usedByText,
                        string.Join(", ", r.DataClassRefs!)
                    }
                };
            })
            .ToList();

        var section = new ReportSection
        {
            Heading = "Data Classifications: At Rest",
            Level = 1,
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section identifies classified or regulated data stored in resources (databases, caches, file stores, etc.). " +
                        "Data 'at rest' should have encryption enabled and appropriate access controls. " +
                        "Third-party resources handling classified data require vendor risk assessment and data processing agreements."
                }
            }
        };

        if (rows.Count > 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Info,
                Title = "Classified Data at Rest",
                Message = $"{rows.Count} resource(s) store classified data. Verify encryption at rest and access controls."
            });

            section.Blocks.Add(new TableBlock
            {
                Headers =
                {
                    "Resource",
                    "Type",
                    "Zone",
                    "Used By Services",
                    "Classifications"
                },
                Rows = rows
            });
        }
        else
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Success,
                Title = "No Classified Data at Rest",
                Message = "No classified data is stored in resources."
            });
        }

        return section;
    }

    // ============================================================
    // TRUST BOUNDARY ANALYSIS
    // ============================================================

    private static ReportSection BuildTrustBoundaryAnalysis(CompiledFlightPlan plan)
    {
        var exportById = plan.Interfaces.ToDictionary(i => i.Id, StringComparer.Ordinal);

        var crossings =
            plan.Dependencies
                .Select(d =>
                {
                    var from = plan.Resolve(d, Direction.From);
                    var to = plan.Resolve(d, Direction.To);

                    if (from == null)
                    {
                        Console.Error.WriteLine($"Warning: Unable to resolve 'from' dependency '{d.From}' for trust boundary analysis.");
                        return null;
                    }
                    if (to == null)
                    {
                        Console.Error.WriteLine($"Warning: Unable to resolve 'to' dependency '{d.To}' for trust boundary analysis.");
                        return null;
                    }

                    var fromZone = (from as dynamic)?.ZoneRef ?? "unknown";
                    var toZone = (to as dynamic)?.ZoneRef ?? "unknown";

                    if (fromZone == toZone)
                        return null;

                    var targetType = d.Kind == "service-to-resource" ? "resource" : "service";
                    var details = BuildDependencyDetails(plan, d);
                    
                    var fromService = from as ServiceEntity;
                    var isViolation = IsTrustViolation(plan, d, fromService, fromZone, toZone, exportById);
                    var severity = DetermineViolationSeverity(plan, d, fromService, fromZone, toZone, exportById);

                    return new
                    {
                        d.Kind,
                        d.Access,
                        TargetType = targetType,
                        Source = from.Name,
                        SourceZone = fromZone,
                        Target = to.Name,
                        TargetZone = toZone,
                        Details = details,
                        IsViolation = isViolation,
                        Severity = severity
                    };
                })
                .Where(x => x != null)
                .Select(x => x!)
                .ToList();

        var violations = crossings.Where(x => x.IsViolation).ToList();
        var safeOrReviewable = crossings.Where(x => !x.IsViolation).ToList();
        var documentedExceptions = crossings
            .Where(x => (x.Access ?? string.Empty).Contains("expected", StringComparison.OrdinalIgnoreCase)
                        || (x.Access ?? string.Empty).Contains("exception", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Summary by zone pairs with violation indicator
        var summaryRows =
            crossings
                .GroupBy(x => new { x.SourceZone, x.TargetZone, x.IsViolation })
                .OrderByDescending(g => g.Key.IsViolation)
                .ThenByDescending(g => g.Count())
                .ThenBy(g => g.Key.SourceZone)
                .ThenBy(g => g.Key.TargetZone)
                .Select(g =>
                {
                    var samples = g
                        .Take(3)
                        .Select(x => $"{x.Source} → {x.Target} ({x.Kind})")
                        .ToList();

                    var riskLabel = g.Key.IsViolation ? "⚠️ VIOLATION" : "✓ Acceptable";

                    return new TableRow
                    {
                        Cells =
                        {
                            g.Key.SourceZone,
                            g.Key.TargetZone,
                            riskLabel,
                            g.Count().ToString(),
                            string.Join("; ", samples)
                        }
                    };
                })
                .ToList();

        // Detail rows with severity
        var detailRows =
            crossings
                .OrderByDescending(x => x.IsViolation)
                .ThenBy(x => x.Severity)
                .ThenBy(x => x.SourceZone)
                .ThenBy(x => x.TargetZone)
                .ThenBy(x => x.Source)
                .ThenBy(x => x.Target)
                .Select(x => new TableRow
                {
                    Cells =
                    {
                        x.Source,
                        x.SourceZone,
                        x.Target,
                        x.TargetZone,
                        x.IsViolation ? "⚠️ VIOLATION" : "✓ OK",
                        x.Severity,
                        x.Details
                    }
                })
                .ToList();

        var section = new ReportSection
        {
            Heading = "Trust Boundary Crossings & Violations",
            Level = 1,
            Anchor = "trust-boundary-analysis",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section analyzes all zone boundary crossings and identifies trust violations. " +
                        "Violations include: (1) public/external services calling non-external exports, (2) internal services persisting data to lower-trust zones, " +
                        "and (3) unexpected cross-zone dependencies. API gateways and proxies bridging public→internal are expected patterns. " +
                        "Dependencies marked with 'access: expected' or 'access: exception' in the Flight Plan are documented but still reviewed."
                }
            }
        };

        if (violations.Count > 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Warning,
                Title = "Trust Boundary Violations Detected",
                Message = $"{violations.Count} interaction(s) violate zone trust boundaries. These require security review, compensating controls, or architectural changes."
            });
        }
        else
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Success,
                Title = "No Trust Violations",
                Message = "All zone boundary crossings follow expected trust patterns (same-level or higher-to-lower trust)."
            });
        }

        if (documentedExceptions.Count > 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Warning,
                Title = "Documented Exceptions Present",
                Message = $"{documentedExceptions.Count} crossing(s) are marked as access: expected/exception. These are allowed, but should be periodically reviewed and justified."
            });
        }

        section.Blocks.Add(new ParagraphBlock
        {
            Text =
                $"Summary: {crossings.Count} total zone crossings ({violations.Count} violations, {safeOrReviewable.Count} acceptable). " +
                "Zone trust ranking: external/public < internal < restricted."
        });

        section.Blocks.Add(new TableBlock
        {
            Headers =
            {
                "Source Zone",
                "Target Zone",
                "Risk",
                "Count",
                "Examples"
            },
            Rows = summaryRows
        });

        section.Blocks.Add(new ParagraphBlock
        {
            Text = "Detailed list of all zone boundary crossings with violation indicators and severity assessment:"
        });

        section.Blocks.Add(new TableBlock
        {
            Headers =
            {
                "Source",
                "Source Zone",
                "Target",
                "Target Zone",
                "Status",
                "Severity",
                "Details"
            },
            Rows = detailRows
        });

        return section;
    }

    // ============================================================
    // INTERFACE & CONTRACT COMPLIANCE
    // ============================================================

    private static ReportSection BuildInterfaceContractCompliance(CompiledFlightPlan plan)
    {
        var exportById = plan.Interfaces.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var violations = new List<(string From, string To, string Issue, string Severity)>();

        static string NormalizeAuth(string? auth)
        {
            return (auth ?? string.Empty).Trim().ToLowerInvariant();
        }

        static int SeverityRank(string severity)
        {
            return severity.ToUpperInvariant() switch
            {
                "CRITICAL" => 50,
                "HIGH" => 40,
                "MEDIUM" => 30,
                "WARNING" => 25,
                "LOW" => 20,
                _ => 10
            };
        }

        foreach (var dep in plan.Dependencies)
        {
            if (dep.Kind == "service-to-service")
            {
                var fromService = plan.Entities.Services.FirstOrDefault(s => s.Id == dep.From);
                var fromZone = fromService?.ZoneRef ?? string.Empty;

                var targetSvcId = SplitServiceId(dep.To);
                if (targetSvcId is null)
                {
                    violations.Add((dep.From, dep.To, "Invalid service dependency target", "HIGH"));
                    continue;
                }

                // Check if export exists
                if (!exportById.TryGetValue(dep.To, out var export))
                {
                    violations.Add((dep.From, dep.To, "Target export not declared in Flight Plan", "HIGH"));
                }
                else
                {
                    // Check if export belongs to the correct service
                    if (!string.Equals(export.ServiceRef, targetSvcId, StringComparison.Ordinal))
                    {
                        violations.Add((dep.From, dep.To, 
                            $"Export belongs to '{export.ServiceRef}', not '{targetSvcId}'", "HIGH"));
                    }

                    // Check visibility for low-trust callers (UNLESS it's an API gateway)
                    var visibility = export.Visibility ?? string.Empty;
                    if (IsLowTrust(fromZone) && !visibility.Equals("external", StringComparison.OrdinalIgnoreCase))
                    {
                        // API Gateways bridging public→internal are expected, not violations
                        if (fromService != null && !IsApiGatewayOrProxy(fromService))
                        {
                            violations.Add((dep.From, dep.To, 
                                $"Low-trust caller accessing non-external export (visibility={visibility})", "CRITICAL"));
                        }
                    }
                }

                // If this dependency was derived from exports.*.forwardsTo, ensure the forwarding export's
                // auth matches the upstream export's auth.
                if (!string.IsNullOrWhiteSpace(dep.DerivedFrom) &&
                    dep.DerivedFrom.Equals("forwardsTo", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(dep.FromExportId))
                {
                    if (exportById.TryGetValue(dep.FromExportId, out var forwardingExport) &&
                        exportById.TryGetValue(dep.To, out var forwardedToExport))
                    {
                        var fromAuth = NormalizeAuth(forwardingExport.Auth);
                        var toAuth = NormalizeAuth(forwardedToExport.Auth);

                        if (!string.Equals(fromAuth, toAuth, StringComparison.Ordinal))
                        {
                            violations.Add((dep.FromExportId, dep.To,
                                $"forwardsTo auth mismatch (from={forwardingExport.Auth ?? "unspecified"}, to={forwardedToExport.Auth ?? "unspecified"})",
                                "WARNING"));
                        }
                    }
                    else
                    {
                        // Keep this as a warning: missing provenance exports is a modeling/compilation issue.
                        violations.Add((dep.FromExportId, dep.To,
                            "forwardsTo auth check skipped (missing forwarding or target export)",
                            "WARNING"));
                    }
                }

                // Check if target service exists
                if (!plan.Entities.Services.Any(s => s.Id == targetSvcId))
                {
                    violations.Add((dep.From, targetSvcId, "Missing upstream service in Flight Plan", "HIGH"));
                }
            }
            else if (dep.Kind == "service-to-resource")
            {
                // Check if resource exists
                if (!plan.Entities.Resources.Any(r => r.Id == dep.To))
                {
                    violations.Add((dep.From, dep.To, "Missing resource in Flight Plan", "HIGH"));
                }
            }
        }

        // External consumers of exports (informational metadata) should only consume externally-visible exports.
        foreach (var export in plan.Interfaces)
        {
            if (export.Consumers == null || export.Consumers.Count == 0) continue;

            var visibility = export.Visibility ?? string.Empty;
            if (!visibility.Equals("external", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var consumer in export.Consumers.Where(c => !string.IsNullOrWhiteSpace(c)))
                {
                    violations.Add((consumer, export.Id,
                        $"External consumer requires export visibility=external (visibility={visibility})",
                        "CRITICAL"));
                }
            }
        }

        var section = new ReportSection
        {
            Heading = "Interface & Contract Compliance",
            Level = 1,
            Anchor = "interface-contract-compliance",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section validates that all declared dependencies have valid targets: " +
                        "service-to-service dependencies must reference declared exports, " +
                        "export interfaces must belong to the correct service, " +
                        "and external/public services must only call externally-visible exports. " +
                        "This is design-time validation of Flight Plan declarations, not runtime contract verification."
                },
                new CalloutBlock
                {
                    CalloutType = CalloutKind.Info,
                    Title = "Architecture Validation",
                    Message = 
                        "These checks validate Flight Plan internal consistency. "
                }
            }
        };

        if (violations.Count > 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Warning,
                Title = "Contract Issues Detected",
                Message = $"{violations.Count} interface or contract issue(s) found. Review Flight Plan declarations for completeness and accuracy."
            });

            var table = new TableBlock
            {
                Headers = { "From Service", "Target", "Issue", "Severity" }
            };

            foreach (var v in violations.OrderByDescending(x => SeverityRank(x.Severity)).ThenBy(x => x.From).ThenBy(x => x.To))
            {
                table.Rows.Add(new TableRow
                {
                    Cells = { v.From, v.To, v.Issue, v.Severity }
                });
            }

            section.Blocks.Add(table);
        }
        else
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Success,
                Title = "All Contracts Valid",
                Message = "All declared dependencies reference valid exports, services, and resources."
            });
        }

        return section;
    }

    // ============================================================
    // THIRD-PARTY DEPENDENCIES
    // ============================================================

    private static ReportSection BuildThirdPartyDependencies(CompiledFlightPlan plan)
    {
        var rows =
            plan.Entities.Resources
                .Select(r => new { Resource = r, Platform = plan.GetPlatformById(r.PlatformRef ?? string.Empty) })
                .Where(x => x.Platform?.Category?.Equals("saas", StringComparison.OrdinalIgnoreCase) == true)
                .Select(x =>
                {
                    var usedByServiceIds = plan.Dependencies
                        .Where(d => d.Kind == "service-to-resource" && d.To == x.Resource.Id)
                        .Select(d => d.From)
                        .Distinct()
                        .OrderBy(id => id)
                        .ToList();

                    var classifications = x.Resource.DataClassRefs != null && x.Resource.DataClassRefs.Any()
                        ? string.Join(", ", x.Resource.DataClassRefs)
                        : "none";

                    return new TableRow
                    {
                        Cells =
                        {
                            x.Resource.Name,
                            x.Platform?.Name ?? x.Resource.PlatformRef ?? "unknown",
                            x.Resource.ZoneRef ?? "unknown",
                            usedByServiceIds.Count.ToString(),
                            classifications
                        }
                    };
                })
                .ToList();

        return new ReportSection
        {
            Heading = "Third-Party Dependencies",
            Level = 1,
            Anchor = "third-party-dependencies",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "This section highlights integrations with third-party systems, which introduce " +
                        "external dependency and vendor risk."
                },

                new TableBlock
                {
                    Headers =
                    {
                        "Resource",
                        "Vendor / Platform",
                        "Zone",
                        "Used By (#services)",
                        "Classifications Shared"
                    },
                    Rows = rows
                }
            }
        };
    }

    // ============================================================
    // OWNERSHIP & RESPONSIBILITY
    // ============================================================

    private static ReportSection BuildOwnershipSummary(CompiledFlightPlan plan)
    {
        var unownedCount = plan.Entities.Services.Count(s => string.IsNullOrWhiteSpace(s.OwnerRef));

        var rows =
            plan.Entities.Services
                .OrderBy(s => string.IsNullOrWhiteSpace(s.OwnerRef) ? 0 : 1)
                .ThenBy(s => s.OwnerRef ?? string.Empty)
                .ThenBy(s => s.Name)
                .Select(s => new TableRow
                {
                    Cells =
                    {
                        s.Name,
                        s.OwnerRef ?? "unowned",
                        s.ZoneRef ?? "unknown",
                        s.PlatformRef ?? "unspecified"
                    }
                })
                .ToList();

        var section = new ReportSection
        {
            Heading = "Ownership & Responsibility",
            Level = 1,
            Anchor = "ownership-summary",
            Blocks =
            {
                new ParagraphBlock
                {
                    Text =
                        "Clear ownership is critical for accountability and incident response. " +
                        "The following table maps services to responsible teams."
                }
            }
        };

        if (unownedCount > 0)
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Warning,
                Title = "Unowned Services Detected",
                Message = $"{unownedCount} service(s) have no owner. Assign ownership to reduce operational and security risk."
            });
        }
        else
        {
            section.Blocks.Add(new CalloutBlock
            {
                CalloutType = CalloutKind.Success,
                Title = "All Services Have Owners",
                Message = "All services have designated owners for accountability and incident response."
            });
        }

        section.Blocks.Add(new TableBlock
        {
            Headers =
            {
                "Service",
                "Owning Team",
                "Zone",
                "Platform"
            },
            Rows = rows
        });

        return section;
    }

    private static bool IsExternallyExposed(ServiceExport export)
    {
        // External exposure is signaled via visibility when provided.
        // Common values: public, external, partner, internal.
        return export.Visibility != null &&
               (export.Visibility.Equals("public", StringComparison.OrdinalIgnoreCase) ||
                export.Visibility.Equals("external", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildDependencyDetails(CompiledFlightPlan plan, Dependency dep)
    {
        if (dep.Kind == "service-to-service")
        {
            var iface = plan.Interfaces.FirstOrDefault(i => i.Id == dep.To);
            if (iface == null)
                return dep.To;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(iface.Name)) parts.Add($"export {iface.Name}");
            if (!string.IsNullOrWhiteSpace(iface.Visibility)) parts.Add(iface.Visibility);
            if (!string.IsNullOrWhiteSpace(iface.Protocol)) parts.Add($"protocol {iface.Protocol}");
            if (!string.IsNullOrWhiteSpace(iface.Auth)) parts.Add($"auth {iface.Auth}");
            if (iface.DataClassRefs is { Count: > 0 }) parts.Add($"classifications {iface.DataClassRefs.Count}");
            return string.Join(", ", parts);
        }

        if (dep.Kind == "service-to-resource")
        {
            var resource = plan.Entities.Resources.FirstOrDefault(r => r.Id == dep.To);
            if (resource == null)
                return dep.To;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(resource.ResourceKind)) parts.Add($"kind {resource.ResourceKind}");
            if (resource.DataClassRefs is { Count: > 0 }) parts.Add($"classifications {resource.DataClassRefs.Count}");
            if (!string.IsNullOrWhiteSpace(dep.Access)) parts.Add($"access {dep.Access}");
            return string.Join(", ", parts);
        }

        return dep.Kind;
    }

    // ============================================================
    // ZONE TRUST HELPERS
    // ============================================================

    private static bool IsTrustViolation(
        CompiledFlightPlan plan,
        Dependency dep,
        ServiceEntity? fromService,
        string fromZone,
        string toZone,
        Dictionary<string, ServiceExport> exportById)
    {
        // Explicit override: allow documenting expected/exception trust crossings.
        // These should still be visible in the report, but not flagged as violations.
        var access = dep.Access ?? string.Empty;
        if (access.Contains("expected", StringComparison.OrdinalIgnoreCase) ||
            access.Contains("exception", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fromRank = ZoneRank(fromZone);
        var toRank = ZoneRank(toZone);

        // Service-to-service dependencies
        if (dep.Kind == "service-to-service")
        {
            // API Gateway pattern: public/external → internal is EXPECTED (not a violation)
            // This is the correct architecture for ingress controllers, proxies, load balancers
            if (fromRank < toRank && fromService != null && IsApiGatewayOrProxy(fromService))
                return false;

            // Check export visibility mismatch (critical for non-gateway services)
            if (exportById.TryGetValue(dep.To, out var export))
            {
                var visibility = export.Visibility ?? string.Empty;
                // Low-trust caller (public/external) calling non-external export = VIOLATION
                if (IsLowTrust(fromZone) && !visibility.Equals("external", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // General lower-trust → higher-trust call without gateway role = potential violation
            if (fromRank < toRank)
                return true;
        }

        // Service-to-resource dependencies
        if (dep.Kind == "service-to-resource")
        {
            // Data flowing OUT to less-trusted zone = HIGH RISK (data exfiltration)
            if (fromRank > toRank)
                return true;

            // Data flowing IN from less-trusted zone to higher-trust storage = depends on context
            // Usually acceptable if properly validated/sanitized
            return false;
        }

        return false;
    }

    private static int ZoneRank(string zone)
    {
        if (string.IsNullOrWhiteSpace(zone) || zone.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return 2; // Default to internal-ish

        if (zone.Equals("restricted", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (zone.Equals("internal", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (zone.Equals("public", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (zone.Equals("external", StringComparison.OrdinalIgnoreCase))
            return 0;

        return 2; // Default to internal
    }

    private static bool IsLowTrust(string zone)
    {
        return zone.Equals("public", StringComparison.OrdinalIgnoreCase)
            || zone.Equals("external", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApiGatewayOrProxy(ServiceEntity service)
    {
        // Detect API gateways, proxies, load balancers, and ingress controllers by name patterns
        var name = service.Name?.ToLowerInvariant() ?? string.Empty;
        var id = service.Id?.ToLowerInvariant() ?? string.Empty;
        
        return name.Contains("api-gateway") ||
               name.Contains("apigateway") ||
               name.Contains("gateway") ||
               name.Contains("proxy") ||
               name.Contains("ingress") ||
               name.Contains("load-balancer") ||
               name.Contains("loadbalancer") ||
               id.Contains("api-gateway") ||
               id.Contains("gateway") ||
               id.Contains("proxy") ||
               id.Contains("ingress");
    }

    private static string DetermineViolationSeverity(
        CompiledFlightPlan plan,
        Dependency dep,
        ServiceEntity? fromService,
        string fromZone,
        string toZone,
        Dictionary<string, ServiceExport> exportById)
    {
        // Check for explicit override
        var access = dep.Access ?? string.Empty;
        if (access.Contains("expected", StringComparison.OrdinalIgnoreCase) ||
            access.Contains("exception", StringComparison.OrdinalIgnoreCase))
        {
            return "Expected (documented exception)";
        }

        var fromRank = ZoneRank(fromZone);
        var toRank = ZoneRank(toZone);

        // Not a violation - provide reason why it's acceptable
        if (!IsTrustViolation(plan, dep, fromService, fromZone, toZone, exportById))
        {
            // Service-to-service patterns
            if (dep.Kind == "service-to-service")
            {
                // API Gateway bridging zones
                if (fromRank < toRank && fromService != null && IsApiGatewayOrProxy(fromService))
                    return "OK: API gateway/proxy pattern";

                // Higher or equal trust level calling lower/equal
                if (fromRank >= toRank)
                    return "OK: Higher/equal trust level";

                // Shouldn't reach here, but safe fallback
                return "OK: Acceptable pattern";
            }

            // Service-to-resource patterns
            if (dep.Kind == "service-to-resource")
            {
                // Same zone or higher-trust accessing resource
                if (fromRank >= toRank)
                    return "OK: Same/higher trust zone";

                // Inbound data flow (lower-trust to higher-trust storage)
                return "OK: Inbound data flow";
            }

            return "OK: Acceptable";
        }

        // Service-to-service violations
        if (dep.Kind == "service-to-service")
        {
            // CRITICAL: Public/external calling non-external export (export visibility mismatch)
            if (IsLowTrust(fromZone) && exportById.TryGetValue(dep.To, out var export))
            {
                var visibility = export.Visibility ?? string.Empty;
                if (!visibility.Equals("external", StringComparison.OrdinalIgnoreCase))
                    return "CRITICAL - Export visibility mismatch";
            }

            // MEDIUM: Non-gateway service crossing trust boundary
            if (fromRank < toRank)
                return "MEDIUM - Review auth/authz";
        }

        // Service-to-resource violations
        if (dep.Kind == "service-to-resource")
        {
            // HIGH: Internal service persisting to public/external resource (data exfiltration risk)
            if (fromRank > toRank)
                return "HIGH - Data flowing to lower-trust zone";

            // MEDIUM: Other resource access patterns
            return "MEDIUM - Review resource access";
        }

        return "MEDIUM";
    }

    private static string? SplitServiceId(string serviceExport)
    {
        if (string.IsNullOrWhiteSpace(serviceExport)) return null;
        var idx = serviceExport.IndexOf('/');
        return idx < 0 ? null : serviceExport[..idx];
    }
}
