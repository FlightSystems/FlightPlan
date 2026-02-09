using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FlightPlan;

public class CompilationResult
{
    public CompiledFlightPlan? Plan { get; set; }
    public List<string> Errors { get; set; } = [];
}

public class FlightPlanCompiler
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public CompilationResult Compile(string yamlText, string basePath)
    {
        var errors = new List<string>();

        // Parse main YAML
        var mainDoc = _deserializer.Deserialize<dynamic>(yamlText);

        // Load uses
        var dataClasses = new Dictionary<string, dynamic>();
        var platforms = new Dictionary<string, dynamic>();
        var resourcesFromUses = new Dictionary<string, dynamic>();
        var servicesFromUses = new Dictionary<string, dynamic>();
        var termsFromUses = new Dictionary<string, dynamic>();
        var toolingFromUses = new Dictionary<string, dynamic>();
        var useDocs = new Dictionary<string, dynamic>();
        if (mainDoc.ContainsKey("uses"))
        {
            foreach (var use in mainDoc["uses"])
            {
                var usePath = Path.Combine(basePath, use);
                var useText = File.ReadAllText(usePath);
                var useDoc = _deserializer.Deserialize<dynamic>(useText);

                if (useDoc.ContainsKey("dataClasses"))
                {
                    foreach (var kvp in useDoc["dataClasses"])
                    {
                        dataClasses[kvp.Key] = kvp.Value;
                    }
                }
                if (useDoc.ContainsKey("platforms"))
                {
                    foreach (var kvp in useDoc["platforms"])
                    {
                        platforms[kvp.Key] = kvp.Value;
                    }
                }

                if (useDoc.ContainsKey("resources"))
                {
                    foreach (var kvp in useDoc["resources"])
                    {
                        resourcesFromUses[kvp.Key] = kvp.Value;
                    }
                }

                if (useDoc.ContainsKey("services"))
                {
                    foreach (var kvp in useDoc["services"])
                    {
                        servicesFromUses[kvp.Key] = kvp.Value;
                    }
                }

                if (useDoc.ContainsKey("terms"))
                {
                    foreach (var kvp in useDoc["terms"])
                    {
                        termsFromUses[kvp.Key] = kvp.Value;
                    }
                }

                if (useDoc.ContainsKey("tooling"))
                {
                    foreach (var kvp in useDoc["tooling"])
                    {
                        toolingFromUses[kvp.Key] = kvp.Value;
                    }
                }
                if (useDoc.ContainsKey("teams"))
                {
                    useDocs["teams"] = useDoc;
                }
                if (useDoc.ContainsKey("environments"))
                {
                    useDocs["environments"] = useDoc;
                }
                if (useDoc.ContainsKey("zones"))
                {
                    useDocs["zones"] = useDoc;
                }
            }
        }

        // Now, build entities
        var entities = new EntityCollection();

        Dictionary<string, string>? ParseToolchainMap(dynamic doc, string context)
        {
            if (doc == null || !doc.ContainsKey("toolchain"))
                return null;

            var map = doc["toolchain"];
            if (map == null)
                return null;

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kvp in map)
            {
                var key = kvp.Key as string;
                if (string.IsNullOrWhiteSpace(key))
                {
                    errors.Add($"Invalid toolchain key in {context}; expected non-empty string.");
                    continue;
                }

                var raw = kvp.Value as string;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    errors.Add($"Invalid toolchain value for '{key}' in {context}; expected tooling://<id> string.");
                    continue;
                }

                if (!raw.StartsWith("tooling://", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Invalid toolchain value for '{key}' in {context}; expected tooling://<id>, got: {raw}");
                    continue;
                }

                var id = ParseRef(raw);
                if (string.IsNullOrWhiteSpace(id))
                {
                    errors.Add($"Invalid toolchain value for '{key}' in {context}; could not parse tooling ref: {raw}");
                    continue;
                }

                result[key] = id;
            }

            return result.Count == 0 ? null : result;
        }

        // Teams
        if (mainDoc.ContainsKey("teams") || useDocs.ContainsKey("teams"))
        {
            var doc = mainDoc.ContainsKey("teams") ? mainDoc["teams"] : useDocs["teams"]["teams"];
            foreach (var kvp in doc)
            {
                var team = kvp.Value;
                var annotations = team.ContainsKey("annotations")
                    ? ConvertYamlMapToStringObjectDictionary(team["annotations"])
                    : null;

                // Allow teams to define an on-call route at the team level.
                // This can then be inherited by services owned by the team.
                var teamOnCall = team.ContainsKey("onCall") ? team["onCall"] as string : null;
                if (!string.IsNullOrWhiteSpace(teamOnCall))
                {
                    annotations ??= new Dictionary<string, object>(StringComparer.Ordinal);
                    var nonNullAnnotations = annotations;

                    Dictionary<string, object> ownershipMap;
                    if (nonNullAnnotations!.TryGetValue("ownership", out object? existingOwnership) && existingOwnership is Dictionary<string, object>)
                        ownershipMap = (Dictionary<string, object>)existingOwnership;
                    else
                    {
                        ownershipMap = new(StringComparer.Ordinal);
                        nonNullAnnotations["ownership"] = ownershipMap;
                    }

                    ownershipMap["onCall"] = teamOnCall;
                }

                entities.Teams.Add(new TeamEntity
                {
                    Id = kvp.Key,
                    Type = "team",
                    Name = kvp.Key,
                    Description = team.ContainsKey("description") ? team["description"] : null,
                    Annotations = annotations
                });
            }
        }

        // Environments
        if (mainDoc.ContainsKey("environments") || useDocs.ContainsKey("environments"))
        {
            var doc = mainDoc.ContainsKey("environments") ? mainDoc["environments"] : useDocs["environments"]["environments"];
            foreach (var kvp in doc)
            {
                entities.Environments.Add(new EnvironmentEntity
                {
                    Id = kvp.Key,
                    Type = "environment",
                    Name = kvp.Key,
                    Description = kvp.Value.ContainsKey("description") ? kvp.Value["description"] : null,
                    ForeignRefs = kvp.Value.ContainsKey("foreignRefs")
                        ? ((IEnumerable<object>)kvp.Value["foreignRefs"]).Cast<string>().ToList()
                        : null,
                    PromotesTo = kvp.Value.ContainsKey("promotesTo")
                        ? kvp.Value["promotesTo"] is string single
                            ? [single]
                            : ((IEnumerable<object>)kvp.Value["promotesTo"]).Cast<string>().ToList()
                        : null
                });
            }
        }

        // Zones
        if (mainDoc.ContainsKey("zones") || useDocs.ContainsKey("zones"))
        {
            var doc = mainDoc.ContainsKey("zones") ? mainDoc["zones"] : useDocs["zones"]["zones"];
            foreach (var kvp in doc)
            {
                entities.Zones.Add(new ZoneEntity
                {
                    Id = kvp.Key,
                    Type = "zone",
                    Name = kvp.Key,
                    Description = kvp.Value.ContainsKey("description") ? kvp.Value["description"] : null
                });
            }
        }

        // Resources (from uses + main; main overrides)
        if (mainDoc.ContainsKey("resources") || resourcesFromUses.Count > 0)
        {
            var resourceById = new Dictionary<string, ResourceEntity>(StringComparer.Ordinal);

            void UpsertResource(dynamic kvp)
            {
                var id = (string)kvp.Key;
                var res = kvp.Value;
                resourceById[id] = new ResourceEntity
                {
                    Id = id,
                    Type = "resource",
                    Name = id,
                    Description = res.ContainsKey("description") ? res["description"] : null,
                    ZoneRef = ParseRef(res.ContainsKey("zone") ? res["zone"] : null),
                    OwnerRef = ParseRef(res.ContainsKey("owner") ? res["owner"] : null),
                    PlatformRef = ParseRef(res.ContainsKey("platform") ? res["platform"] : null),
                    DataClassRefs = res.ContainsKey("dataClasses")
                        ? ((IEnumerable<object>)res["dataClasses"]).Cast<string>()
                            .Select(dc => ParseRef(dc))
                            .Where(x => x != null)
                            .Select(x => x!)
                            .ToList()
                        : null,
                    Annotations = res.ContainsKey("annotations") ? ConvertYamlMapToStringObjectDictionary(res["annotations"]) : null,
                    ResourceKind = res.ContainsKey("type") ? res["type"] : null
                };
            }

            foreach (var kvp in resourcesFromUses)
            {
                UpsertResource(kvp);
            }

            if (mainDoc.ContainsKey("resources"))
            {
                foreach (var kvp in mainDoc["resources"])
                {
                    UpsertResource(kvp);
                }
            }

            foreach (var resource in resourceById.Values)
            {
                entities.Resources.Add(resource);
            }
        }

        // Services (from uses + main; main overrides)
        var services = new Dictionary<string, ServiceEntity>(StringComparer.Ordinal);
        var serviceDocsById = new Dictionary<string, dynamic>(StringComparer.Ordinal);

        foreach (var kvp in servicesFromUses)
        {
            serviceDocsById[kvp.Key] = kvp.Value;
        }

        if (mainDoc.ContainsKey("services"))
        {
            foreach (var kvp in mainDoc["services"])
            {
                serviceDocsById[kvp.Key] = kvp.Value;
            }
        }

        foreach (var kvp in serviceDocsById)
        {
            var svc = kvp.Value;
            var service = new ServiceEntity
            {
                Id = kvp.Key,
                Type = "service",
                Name = kvp.Key,
                Description = svc.ContainsKey("description") ? svc["description"] : null,
                ZoneRef = ParseRef(svc.ContainsKey("zone") ? svc["zone"] : null),
                OwnerRef = ParseRef(svc.ContainsKey("owner") ? svc["owner"] : null),
                PlatformRef = ParseRef(svc.ContainsKey("platform") ? svc["platform"] : null),
                DataClassRefs = null, // services don't have dataClasses directly
                ForeignRefs = svc.ContainsKey("foreignRefs")
                    ? ((IEnumerable<object>)svc["foreignRefs"]).Cast<string>().ToList()
                    : null,
                RepoUrl = svc.ContainsKey("repoUrl")
                    ? (svc["repoUrl"] as string)
                    : (svc.ContainsKey("sourceControlUrl") ? (svc["sourceControlUrl"] as string) : null),
                Annotations = svc.ContainsKey("annotations") ? ConvertYamlMapToStringObjectDictionary(svc["annotations"]) : null,
                Toolchain = ParseToolchainMap(svc, $"service '{kvp.Key}'")
            };

            services[kvp.Key] = service;
            entities.Services.Add(service);
        }

        // Tooling (from uses + main; main overrides)
        if (mainDoc.ContainsKey("tooling") || toolingFromUses.Count > 0)
        {
            var toolingDocsById = new Dictionary<string, dynamic>(StringComparer.Ordinal);
            foreach (var kvp in toolingFromUses)
            {
                toolingDocsById[kvp.Key] = kvp.Value;
            }

            if (mainDoc.ContainsKey("tooling"))
            {
                foreach (var kvp in mainDoc["tooling"])
                {
                    toolingDocsById[kvp.Key] = kvp.Value;
                }
            }

            foreach (var kvp in toolingDocsById)
            {
                var tool = kvp.Value;
                entities.Tooling.Add(new ToolingEntity
                {
                    Id = kvp.Key,
                    Type = "tooling",
                    Name = kvp.Key,
                    Description = tool.ContainsKey("description") ? tool["description"] : null,
                    OwnerRef = ParseRef(tool.ContainsKey("owner") ? tool["owner"] : null),
                    ZoneRef = ParseRef(tool.ContainsKey("zone") ? tool["zone"] : null),
                    Kind = tool.ContainsKey("type") ? tool["type"] : null,
                    Provider = tool.ContainsKey("provider") ? tool["provider"] : null,
                    Organization = tool.ContainsKey("organization") ? tool["organization"] : null,
                    Url = tool.ContainsKey("url") ? tool["url"] : null,
                    ForeignRefs = tool.ContainsKey("foreignRefs")
                        ? ((IEnumerable<object>)tool["foreignRefs"]).Cast<string>().ToList()
                        : null,
                    Annotations = tool.ContainsKey("annotations") ? ConvertYamlMapToStringObjectDictionary(tool["annotations"]) : null
                });
            }
        }

        // Now, process exports and dependencies
        var interfaces = new List<ServiceExport>();
        var dependencies = new List<Dependency>();
        foreach (var svcKvp in serviceDocsById)
        {
            var svcId = svcKvp.Key;
            var svc = svcKvp.Value;
            if (!services.TryGetValue(svcId, out var service))
            {
                errors.Add($"Service doc present but entity not created: {svcId}");
                continue;
            }

            // Dependencies implied by exports forwarding (collected first; applied after dependsOn so we can dedupe)
            var forwardedDependencies = new List<Dependency>();
            if (svc.ContainsKey("exports"))
            {
                foreach (var expKvp in svc["exports"])
                {
                    var expName = expKvp.Key;
                    var exp = expKvp.Value;

                    string visibility;
                    if (!exp.ContainsKey("visibility"))
                    {
                        visibility = "internal";
                    }
                    else if (exp["visibility"] is string visString && !string.IsNullOrWhiteSpace(visString))
                    {
                        visibility = visString;
                    }
                    else
                    {
                        errors.Add($"Invalid visibility value for export {svcId}/{expName}; expected non-empty string.");
                        visibility = "internal";
                    }

                    var export = new ServiceExport
                    {
                        Id = $"{svcId}/{expName}",
                        ServiceRef = svcId,
                        Name = expName,
                        Protocol = exp.ContainsKey("protocol") ? exp["protocol"] : null,
                        Auth = exp.ContainsKey("auth") ? exp["auth"] : null,
                        Visibility = visibility,
                        DataClassRefs = exp.ContainsKey("dataClasses") ? ((IEnumerable<object>)exp["dataClasses"]).Cast<string>().Select(dc => ParseRef(dc)).Where(x => x != null).Select(x => x!).ToList() : null,
                        Consumers = exp.ContainsKey("consumers")
                            ? (exp["consumers"] is string singleConsumer
                                ? [singleConsumer]
                                : (exp["consumers"] is IEnumerable<object> consumerList
                                    ? consumerList.Cast<string>().ToList()
                                    : null))
                            : null
                    };

                    if (exp.ContainsKey("consumers") && export.Consumers == null)
                    {
                        errors.Add($"Invalid consumers value for export {svcId}/{expName}; expected string or list of strings.");
                    }
                    interfaces.Add(export);
                    service.ExportIds.Add(export.Id);

                    // Optional: exports.<name>.forwardsTo: svc://other-service/export (or list)
                    if (exp.ContainsKey("forwardsTo"))
                    {
                        IEnumerable<string> forwardUris;
                        if (exp["forwardsTo"] is string singleForward)
                        {
                            forwardUris = [singleForward];
                        }
                        else if (exp["forwardsTo"] is IEnumerable<object> forwardList)
                        {
                            forwardUris = forwardList.Cast<string>();
                        }
                        else
                        {
                            errors.Add($"Invalid forwardsTo value for export {svcId}/{expName}; expected string or list of strings.");
                            continue;
                        }

                        foreach (var forwardUri in forwardUris)
                        {
                            if (string.IsNullOrWhiteSpace(forwardUri)) continue;

                            var (kind, to, uriError) = ParseUri(forwardUri);
                            if (uriError != null)
                            {
                                errors.Add(uriError);
                                continue;
                            }

                            if (kind != "service-to-service")
                            {
                                errors.Add($"forwardsTo for export {svcId}/{expName} must be a service URI (svc://service/export), got: {forwardUri}");
                                continue;
                            }

                            forwardedDependencies.Add(new Dependency
                            {
                                From = svcId,
                                To = to!,
                                Kind = kind!,
                                Access = null,
                                FromExportId = export.Id,
                                DerivedFrom = "forwardsTo"
                            });
                        }
                    }
                }
            }
            if (svc.ContainsKey("dependsOn"))
            {
                foreach (var dep in svc["dependsOn"])
                {
                    string uri = dep["uri"];
                    var access = dep.ContainsKey("access") ? dep["access"] : null;
                    var (kind, to, uriError) = ParseUri(uri);
                    if (uriError != null)
                    {
                        errors.Add(uriError);
                    }
                    else
                    {
                        dependencies.Add(new Dependency
                        {
                            From = svcId,
                            To = to!,
                            Kind = kind!,
                            Access = access
                        });
                    }
                }
            }

            // Apply forwardsTo-derived dependencies, skipping duplicates (by from/kind/to)
            foreach (var forwarded in forwardedDependencies)
            {
                if (dependencies.Any(d => d.From == forwarded.From && d.Kind == forwarded.Kind && d.To == forwarded.To))
                {
                    continue;
                }

                dependencies.Add(forwarded);
            }
        }

        // Collect referenced dataClasses and platforms
        var referencedDataClasses = new HashSet<string>();
        var referencedPlatforms = new HashSet<string>();

        foreach (var resource in entities.Resources)
        {
            if (resource.PlatformRef != null)
                referencedPlatforms.Add(resource.PlatformRef);
            if (resource.DataClassRefs != null)
                foreach (var dc in resource.DataClassRefs)
                    referencedDataClasses.Add(dc);
        }

        foreach (var service in entities.Services)
        {
            if (service.PlatformRef != null)
                referencedPlatforms.Add(service.PlatformRef);
        }

        foreach (var iface in interfaces)
        {
            if (iface.DataClassRefs != null)
                foreach (var dc in iface.DataClassRefs)
                    referencedDataClasses.Add(dc);
        }

        // DataClasses from uses, only referenced
        foreach (var kvp in dataClasses)
        {
            if (referencedDataClasses.Contains(kvp.Key))
            {
                entities.DataClasses.Add(new DataClassEntity
                {
                    Id = kvp.Key,
                    Type = "dataClass",
                    Name = kvp.Key,
                    Description = kvp.Value.ContainsKey("description") ? kvp.Value["description"] : null
                });
            }
        }

        // Platforms from uses, only referenced
        foreach (var kvp in platforms)
        {
            if (referencedPlatforms.Contains(kvp.Key))
            {
                entities.Platforms.Add(new PlatformEntity
                {
                    Id = kvp.Key,
                    Type = "platform",
                    Name = kvp.Key,
                    Description = kvp.Value.ContainsKey("description") ? kvp.Value["description"] : null,
                    Category = kvp.Value.ContainsKey("category") ? kvp.Value["category"] : null
                });
            }
        }

        // Validate top-level toolchain refs (must refer to known tooling entities)
        var defaultToolchain = ParseToolchainMap(mainDoc, "top-level");
        if (defaultToolchain != null)
        {
            var toolingIds = entities.Tooling.Select(t => t.Id).ToHashSet();
            foreach (var (k, toolingId) in defaultToolchain)
            {
                if (string.IsNullOrWhiteSpace(toolingId) || !toolingIds.Contains(toolingId))
                    errors.Add($"Invalid toolchain ref for top-level key '{k}': tooling '{toolingId}' not found.");
            }
        }

        // Now, validate references
        ValidateReferences(entities, interfaces, dependencies, errors);

        var result = new CompilationResult { Errors = errors };
        if (!errors.Any())
        {
            // Build metadata
            var sourceFiles = new List<string> { "flightplan.yml" };
            if (mainDoc.ContainsKey("uses"))
            {
                foreach (var use in mainDoc["uses"])
                {
                    sourceFiles.Add(use);
                }
            }
            var metadata = new Metadata
            {
                Format = "flightplan/v1",
                CompiledAt = DateTimeOffset.Now,
                SourceFiles = sourceFiles
            };
            var application = new Application
            {
                Name = mainDoc["application"]["name"],
                Type = mainDoc["application"].ContainsKey("type") ? (mainDoc["application"]["type"] as string) : null,
                Domain = mainDoc["application"].ContainsKey("domain") ? (mainDoc["application"]["domain"] as string) : null,
                Description = mainDoc["application"].ContainsKey("description") ? mainDoc["application"]["description"] : null
            };

            DeliveryModel? deliveryModel = null;
            if (mainDoc.ContainsKey("deliveryModel"))
            {
                var dm = mainDoc["deliveryModel"];

                List<string>? primaryUsers = null;
                if (dm.ContainsKey("primaryUsers"))
                {
                    if (dm["primaryUsers"] is string singleUser)
                    {
                        primaryUsers = [singleUser];
                    }
                    else if (dm["primaryUsers"] is IEnumerable<object> userList)
                    {
                        primaryUsers = userList.Cast<string>().ToList();
                    }
                    else
                    {
                        errors.Add("Invalid deliveryModel.primaryUsers value; expected string or list of strings.");
                    }
                }

                deliveryModel = new DeliveryModel
                {
                    Hosting = dm.ContainsKey("hosting") ? (dm["hosting"] as string) : null,
                    ServiceModel = dm.ContainsKey("serviceModel") ? (dm["serviceModel"] as string) : null,
                    Tenancy = dm.ContainsKey("tenancy") ? (dm["tenancy"] as string) : null,
                    PrimaryUsers = primaryUsers
                };
            }

            result.Plan = new CompiledFlightPlan
            {
                Metadata = metadata,
                Application = application,
                DeliveryModel = deliveryModel,
                Entities = entities,
                Interfaces = interfaces,
                Dependencies = dependencies,
                Annotations = mainDoc.ContainsKey("annotations") ? ConvertYamlMapToStringObjectDictionary(mainDoc["annotations"]) : null,
                Toolchain = defaultToolchain,
                Terms = MergeTerms(termsFromUses, mainDoc)
            };
        }
        return result;
    }

    private static Dictionary<string, object>? MergeTerms(Dictionary<string, dynamic> termsFromUses, dynamic mainDoc)
    {
        Dictionary<string, object>? merged = null;

        void MergeFromYamlValue(object? yamlTerms)
        {
            var incoming = ConvertYamlMapToStringObjectDictionary(yamlTerms);
            if (incoming == null || incoming.Count == 0) return;

            merged ??= new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var (termKey, incomingValue) in incoming)
            {
                if (!merged.TryGetValue(termKey, out var existingValue))
                {
                    merged[termKey] = incomingValue;
                    continue;
                }

                if (existingValue is not Dictionary<string, object> existingMap || incomingValue is not Dictionary<string, object> incomingMap)
                {
                    // If either side isn't a map, prefer the incoming value.
                    merged[termKey] = incomingValue;
                    continue;
                }

                // Merge at term-value level, allowing overrides per value.
                foreach (var (termValue, incomingDef) in incomingMap)
                {
                    if (existingMap.TryGetValue(termValue, out var existingDef) &&
                        existingDef is Dictionary<string, object> existingDefMap &&
                        incomingDef is Dictionary<string, object> incomingDefMap)
                    {
                        foreach (var (k, v) in incomingDefMap)
                        {
                            existingDefMap[k] = v;
                        }
                    }
                    else
                    {
                        existingMap[termValue] = incomingDef;
                    }
                }
            }
        }

        foreach (var kvp in termsFromUses)
        {
            // Each kvp is (termKey -> termMap)
            MergeFromYamlValue(new Dictionary<object, object> { [kvp.Key] = kvp.Value });
        }

        if (mainDoc.ContainsKey("terms"))
        {
            MergeFromYamlValue(mainDoc["terms"]);
        }

        return merged;
    }

    private static Dictionary<string, object>? ConvertYamlMapToStringObjectDictionary(object? yamlValue)
    {
        if (yamlValue is null) return null;

        // YamlDotNet typically produces Dictionary<object, object> for maps.
        if (yamlValue is IDictionary<object, object> objDict)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (k, v) in objDict)
            {
                if (k is null) continue;
                var key = k.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key)) continue;
                result[key] = ConvertYamlValue(v);
            }

            return result;
        }

        if (yamlValue is IDictionary<string, object> strDict)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (k, v) in strDict)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                result[k] = ConvertYamlValue(v);
            }

            return result;
        }

        // If someone wrote annotations: "some string" treat it as a single value container.
        return new Dictionary<string, object>(StringComparer.Ordinal) { ["value"] = ConvertYamlValue(yamlValue) };
    }

    private static object ConvertYamlValue(object? value)
    {
        if (value is null) return string.Empty;

        // Preserve common scalar types.
        if (value is string or bool or int or long or double or decimal)
            return value;

        if (value is IDictionary<object, object> map)
            return ConvertYamlMapToStringObjectDictionary(map) ?? new(StringComparer.Ordinal);

        if (value is IDictionary<string, object> strMap)
            return ConvertYamlMapToStringObjectDictionary(strMap) ?? new(StringComparer.Ordinal);

        // Treat sequences as arrays, but avoid splitting strings (which are IEnumerable<char>).
        if (value is IEnumerable<object> list)
            return list.Select(ConvertYamlValue).ToList();

        return value.ToString() ?? string.Empty;
    }

    private string? ParseRef(string? refStr)
    {
        if (string.IsNullOrEmpty(refStr)) return null;
        // e.g., zone://restricted -> restricted
        var parts = refStr.Split("://");
        return parts.Length == 2 ? parts[1] : refStr;
    }

    private (string? kind, string? to, string? error) ParseUri(string uri)
    {
        // e.g., svc://users/public-api -> ("service->service", "users/public-api")
        // res://users-db -> ("service->resource", "users-db")
        var parts = uri.Split("://");
        if (parts.Length != 2) return (null, null, $"Invalid URI: {uri}");
        var scheme = parts[0];
        var path = parts[1];
        string? kind = null;
        string? to = null;
        string? error = null;
        if (scheme == "svc")
        {
            if (!path.Contains('/'))
            {
                error = $"Service URI must be in format svc://service/export, got: {uri}";
            }
            else
            {
                kind = "service-to-service";
                to = path; // full path, e.g., users/public-api
            }
        }
        else if (scheme == "res")
        {
            kind = "service-to-resource";
            to = path; // full path, e.g., users-db
        }
        else
        {
            error = $"Unknown scheme: {scheme}";
        }
        return (kind, to, error);
    }

    private void ValidateReferences(EntityCollection entities, List<ServiceExport> interfaces, List<Dependency> dependencies, List<string> errors)
    {
        var zoneIds = entities.Zones.Select(z => z.Id).ToHashSet();
        var teamIds = entities.Teams.Select(t => t.Id).ToHashSet();
        var platformIds = entities.Platforms.Select(p => p.Id).ToHashSet();
        var toolingIds = entities.Tooling.Select(t => t.Id).ToHashSet();
        var dataClassIds = entities.DataClasses.Select(dc => dc.Id).ToHashSet();
        var resourceIds = entities.Resources.Select(r => r.Id).ToHashSet();
        var serviceIds = entities.Services.Select(s => s.Id).ToHashSet();
        var exportIds = interfaces.Select(i => i.Id).ToHashSet();

        // Validate entities
        foreach (var entity in entities.Zones.Cast<BaseEntity>()
                     .Concat(entities.Teams.Cast<BaseEntity>())
                     .Concat(entities.Environments.Cast<BaseEntity>())
                     .Concat(entities.Platforms.Cast<BaseEntity>())
                     .Concat(entities.DataClasses.Cast<BaseEntity>())
                     .Concat(entities.Resources.Cast<BaseEntity>())
                     .Concat(entities.Services.Cast<BaseEntity>())
                     .Concat(entities.Tooling.Cast<BaseEntity>()))
        {
            if (!string.IsNullOrEmpty(entity.OwnerRef) && !teamIds.Contains(entity.OwnerRef))
                errors.Add($"Invalid owner ref: {entity.OwnerRef} in {entity.Id}");
            if (!string.IsNullOrEmpty(entity.PlatformRef) && !platformIds.Contains(entity.PlatformRef))
                errors.Add($"Invalid platform ref: {entity.PlatformRef} in {entity.Id}");
            if (entity.DataClassRefs != null)
            {
                foreach (var dc in entity.DataClassRefs)
                {
                    if (!dataClassIds.Contains(dc))
                        errors.Add($"Invalid dataClass ref: {dc} in {entity.Id}");
                }
            }
        }

        // Validate interfaces dataClasses
        foreach (var iface in interfaces)
        {
            if (iface.DataClassRefs != null)
            {
                foreach (var dc in iface.DataClassRefs)
                {
                    if (!dataClassIds.Contains(dc))
                        errors.Add($"Invalid dataClass ref: {dc} in interface {iface.Id}");
                }
            }
        }

        // Validate dependencies
        foreach (var dep in dependencies)
        {
            if (!serviceIds.Contains(dep.From))
                errors.Add($"Invalid from service: {dep.From}");
            if (dep.Kind == "service-to-service")
            {
                if (!exportIds.Contains(dep.To))
                    errors.Add($"Invalid to export: {dep.To}");
            }
            else if (dep.Kind == "service-to-resource")
            {
                if (!resourceIds.Contains(dep.To))
                    errors.Add($"Invalid to resource: {dep.To}");
            }
        }

        // Validate toolchain refs (service-level overrides)
        foreach (var svc in entities.Services)
        {
            if (svc.Toolchain == null) continue;
            foreach (var (k, toolingId) in svc.Toolchain)
            {
                if (string.IsNullOrWhiteSpace(toolingId) || !toolingIds.Contains(toolingId))
                    errors.Add($"Invalid toolchain ref for service '{svc.Id}' key '{k}': tooling '{toolingId}' not found.");
            }
        }
    }
}