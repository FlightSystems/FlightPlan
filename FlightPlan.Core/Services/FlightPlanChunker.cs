using System.Text;
using System.Text.Json;
using FlightPlan.Models;

namespace FlightPlan.Services;

/// <summary>
/// Chunks a FlightPlan JSON into semantic pieces for embedding
/// </summary>
public class FlightPlanChunker
{
    public List<FlightPlanChunk> ChunkFlightPlan(JsonDocument plan)
    {
        var chunks = new List<FlightPlanChunk>();
        var root = plan.RootElement;

        // Chunk 0: High-level overview (for broad architectural queries)
        chunks.Add(new FlightPlanChunk
        {
            Id = "overview",
            Type = "overview",
            Content = BuildOverviewSummary(root),
            Metadata = new Dictionary<string, string>
            {
                ["type"] = "overview",
                ["priority"] = "high"
            }
        });

        // Chunk 1: Application metadata
        if (root.TryGetProperty("application", out var app))
        {
            chunks.Add(new FlightPlanChunk
            {
                Id = "application",
                Type = "application",
                Content = BuildApplicationSummary(app),
                Metadata = new Dictionary<string, string>
                {
                    ["type"] = "application"
                }
            });
        }

        // Chunk services
        if (root.TryGetProperty("entities", out var entities))
        {
            if (entities.TryGetProperty("services", out var services))
            {
                foreach (var service in services.EnumerateArray())
                {
                    var serviceId = service.GetProperty("id").GetString() ?? "unknown";
                    chunks.Add(new FlightPlanChunk
                    {
                        Id = $"service:{serviceId}",
                        Type = "service",
                        Content = BuildServiceSummary(service),
                        Metadata = new Dictionary<string, string>
                        {
                            ["type"] = "service",
                            ["id"] = serviceId
                        }
                    });
                }
            }

            // Chunk environments
            if (entities.TryGetProperty("environments", out var environments))
            {
                var envSummary = new StringBuilder();
                envSummary.AppendLine("# Environments");
                foreach (var env in environments.EnumerateArray())
                {
                    var envId = env.GetProperty("id").GetString();
                    var envName = env.TryGetProperty("name", out var n) ? n.GetString() : envId;
                    envSummary.AppendLine($"- {envId}: {envName}");
                    
                    if (env.TryGetProperty("promotesTo", out var promotes))
                    {
                        envSummary.Append($"  Promotes to: ");
                        envSummary.AppendLine(string.Join(", ", promotes.EnumerateArray().Select(p => p.GetString())));
                    }
                }
                
                chunks.Add(new FlightPlanChunk
                {
                    Id = "environments",
                    Type = "environments",
                    Content = envSummary.ToString(),
                    Metadata = new Dictionary<string, string> { ["type"] = "environments" }
                });
            }

            // Chunk resources
            if (entities.TryGetProperty("resources", out var resources))
            {
                foreach (var resource in resources.EnumerateArray())
                {
                    var resourceId = resource.GetProperty("id").GetString() ?? "unknown";
                    chunks.Add(new FlightPlanChunk
                    {
                        Id = $"resource:{resourceId}",
                        Type = "resource",
                        Content = BuildResourceSummary(resource),
                        Metadata = new Dictionary<string, string>
                        {
                            ["type"] = "resource",
                            ["id"] = resourceId
                        }
                    });
                }
            }

            // Chunk teams
            if (entities.TryGetProperty("teams", out var teams))
            {
                var teamSummary = new StringBuilder();
                teamSummary.AppendLine("# Teams");
                foreach (var team in teams.EnumerateArray())
                {
                    var teamId = team.GetProperty("id").GetString();
                    var teamName = team.TryGetProperty("name", out var n) ? n.GetString() : teamId;
                    teamSummary.AppendLine($"- {teamId}: {teamName}");
                }
                
                chunks.Add(new FlightPlanChunk
                {
                    Id = "teams",
                    Type = "teams",
                    Content = teamSummary.ToString(),
                    Metadata = new Dictionary<string, string> { ["type"] = "teams" }
                });
            }

            // Chunk zones
            if (entities.TryGetProperty("zones", out var zones))
            {
                var zoneSummary = new StringBuilder();
                zoneSummary.AppendLine("# Security Zones");
                foreach (var zone in zones.EnumerateArray())
                {
                    var zoneId = zone.GetProperty("id").GetString();
                    var zoneName = zone.TryGetProperty("name", out var n) ? n.GetString() : zoneId;
                    zoneSummary.AppendLine($"- {zoneId}: {zoneName}");
                }
                
                chunks.Add(new FlightPlanChunk
                {
                    Id = "zones",
                    Type = "zones",
                    Content = zoneSummary.ToString(),
                    Metadata = new Dictionary<string, string> { ["type"] = "zones" }
                });
            }
        }

        return chunks;
    }

    private string BuildOverviewSummary(JsonElement root)
    {
        var summary = new StringBuilder();
        summary.AppendLine("# FlightPlan Architecture Overview & Summary");
        summary.AppendLine();
        summary.AppendLine("This is a comprehensive summary of the entire system architecture, providing a bird's-eye view of all major components, services, environments, and infrastructure.");
        summary.AppendLine();
        
        // Application info
        if (root.TryGetProperty("application", out var app))
        {
            if (app.TryGetProperty("name", out var name))
                summary.AppendLine($"**Application:** {name.GetString()}");
            if (app.TryGetProperty("type", out var type))
                summary.AppendLine($"**Type:** {type.GetString()}");
            if (app.TryGetProperty("domain", out var domain))
                summary.AppendLine($"**Domain:** {domain.GetString()}");
            summary.AppendLine();
        }
        
        // Entity counts
        if (root.TryGetProperty("entities", out var entities))
        {
            summary.AppendLine("## System Components:");
            
            if (entities.TryGetProperty("services", out var services))
            {
                var serviceCount = services.GetArrayLength();
                summary.AppendLine($"- **Services:** {serviceCount}");
                
                // Service breakdown by owner
                var servicesByOwner = new Dictionary<string, int>();
                foreach (var service in services.EnumerateArray())
                {
                    if (service.TryGetProperty("ownerRef", out var owner))
                    {
                        var ownerStr = owner.GetString() ?? "unknown";
                        servicesByOwner[ownerStr] = servicesByOwner.GetValueOrDefault(ownerStr) + 1;
                    }
                }
                
                if (servicesByOwner.Any())
                {
                    summary.AppendLine("  - By team:");
                    foreach (var kvp in servicesByOwner.OrderByDescending(x => x.Value))
                    {
                        summary.AppendLine($"    - {kvp.Key}: {kvp.Value} services");
                    }
                }
                
                // Service breakdown by platform
                var servicesByPlatform = new Dictionary<string, int>();
                foreach (var service in services.EnumerateArray())
                {
                    if (service.TryGetProperty("platformRef", out var platform))
                    {
                        var platformStr = platform.GetString() ?? "unknown";
                        servicesByPlatform[platformStr] = servicesByPlatform.GetValueOrDefault(platformStr) + 1;
                    }
                }
                
                if (servicesByPlatform.Any())
                {
                    summary.AppendLine("  - By platform:");
                    foreach (var kvp in servicesByPlatform.OrderByDescending(x => x.Value))
                    {
                        summary.AppendLine($"    - {kvp.Key}: {kvp.Value} services");
                    }
                }
            }
            
            if (entities.TryGetProperty("environments", out var environments))
            {
                summary.AppendLine($"- **Environments:** {environments.GetArrayLength()}");
                var envNames = new List<string>();
                foreach (var env in environments.EnumerateArray())
                {
                    if (env.TryGetProperty("id", out var id))
                        envNames.Add(id.GetString() ?? "");
                }
                summary.AppendLine($"  ({string.Join(" → ", envNames)})");
            }
            
            if (entities.TryGetProperty("resources", out var resources))
            {
                var resourceCount = resources.GetArrayLength();
                summary.AppendLine($"- **Resources:** {resourceCount}");
                
                // Resource breakdown by type
                var resourcesByType = new Dictionary<string, int>();
                foreach (var resource in resources.EnumerateArray())
                {
                    if (resource.TryGetProperty("type", out var type))
                    {
                        var typeStr = type.GetString() ?? "unknown";
                        resourcesByType[typeStr] = resourcesByType.GetValueOrDefault(typeStr) + 1;
                    }
                }
                
                if (resourcesByType.Any())
                {
                    summary.AppendLine("  - By type:");
                    foreach (var kvp in resourcesByType.OrderByDescending(x => x.Value))
                    {
                        summary.AppendLine($"    - {kvp.Key}: {kvp.Value}");
                    }
                }
            }
            
            if (entities.TryGetProperty("teams", out var teams))
            {
                summary.AppendLine($"- **Teams:** {teams.GetArrayLength()}");
            }
            
            if (entities.TryGetProperty("zones", out var zones))
            {
                summary.AppendLine($"- **Security Zones:** {zones.GetArrayLength()}");
            }
        }
        
        summary.AppendLine();
        summary.AppendLine("## Summary");
        summary.AppendLine("This architecture overview summarizes the complete system structure.");
        summary.AppendLine("Use this information for high-level understanding, architectural reviews, or system summaries.");
        summary.AppendLine("For specific details about individual services, environments, or resources, ask targeted questions.");
        
        return summary.ToString();
    }

    private string BuildApplicationSummary(JsonElement app)
    {
        var summary = new StringBuilder();
        summary.AppendLine("# Application Overview");
        
        if (app.TryGetProperty("name", out var name))
            summary.AppendLine($"Name: {name.GetString()}");
        
        if (app.TryGetProperty("type", out var type))
            summary.AppendLine($"Type: {type.GetString()}");
        
        if (app.TryGetProperty("domain", out var domain))
            summary.AppendLine($"Domain: {domain.GetString()}");
        
        if (app.TryGetProperty("description", out var desc))
            summary.AppendLine($"Description: {desc.GetString()}");
        
        return summary.ToString();
    }

    private string BuildServiceSummary(JsonElement service)
    {
        var summary = new StringBuilder();
        
        var id = service.GetProperty("id").GetString();
        summary.AppendLine($"# Service: {id}");
        summary.AppendLine();
        
        string? nameStr = null;
        string? descStr = null;
        
        if (service.TryGetProperty("name", out var name))
        {
            nameStr = name.GetString();
            summary.AppendLine($"**Name:** {nameStr}");
        }
        
        if (service.TryGetProperty("description", out var desc))
        {
            descStr = desc.GetString();
            summary.AppendLine($"**Description:** {descStr}");
        }
        
        // Add prominent alternate phrasings RIGHT AFTER name/description for better semantic matching
        // This is critical because embedding models weight early content more heavily
        if (!string.IsNullOrEmpty(nameStr) || !string.IsNullOrEmpty(descStr))
        {
            summary.AppendLine();
            summary.AppendLine("**Also known as:**");
            
            // Add the ID again for emphasis
            summary.AppendLine($"- {id}");
            
            // Add description as alternate name
            if (!string.IsNullOrEmpty(descStr))
                summary.AppendLine($"- {descStr}");
            
            // For API Gateway services, add word-order variations prominently
            if (id?.Contains("gateway") == true || descStr?.Contains("Gateway") == true)
            {
                // Convert "api-gateway-payer" to "Payer API Gateway"
                var parts = id?.Split('-').Where(p => p != "api" && p != "gateway").ToList() ?? new List<string>();
                if (parts.Any())
                {
                    var reordered = string.Join(" ", parts.Select(p => char.ToUpper(p[0]) + p.Substring(1)));
                    summary.AppendLine($"- {reordered} API Gateway");
                    summary.AppendLine($"- {reordered} Gateway");
                    summary.AppendLine($"- API Gateway for {reordered}");
                }
            }
        }
        
        summary.AppendLine();
        
        if (service.TryGetProperty("platformRef", out var platform))
            summary.AppendLine($"**Platform:** {platform.GetString()}");
        
        if (service.TryGetProperty("ownerRef", out var owner))
            summary.AppendLine($"**Owner:** {owner.GetString()}");
        
        if (service.TryGetProperty("zoneRef", out var zone))
            summary.AppendLine($"**Zone:** {zone.GetString()}");
        
        summary.AppendLine();
        
        // Add resource dependencies prominently
        if (service.TryGetProperty("resourceRefs", out var resourceRefs))
        {
            var refCount = resourceRefs.GetArrayLength();
            if (refCount > 0)
            {
                summary.AppendLine($"**Resource Dependencies:** This service depends on {refCount} resources:");
                foreach (var resourceRef in resourceRefs.EnumerateArray())
                {
                    summary.AppendLine($"  - {resourceRef.GetString()}");
                }
                summary.AppendLine();
            }
            else
            {
                summary.AppendLine("**Resource Dependencies:** None - this service has no resource dependencies.");
                summary.AppendLine();
            }
        }
        else
        {
            summary.AppendLine("**Resource Dependencies:** None - this service has no resource dependencies.");
            summary.AppendLine();
        }
        
        if (service.TryGetProperty("exportIds", out var exports))
        {
            summary.AppendLine($"**Exports ({exports.GetArrayLength()}):**");
            foreach (var export in exports.EnumerateArray())
            {
                summary.AppendLine($"  - {export.GetString()}");
            }
        }
        
        return summary.ToString();
    }

    private string BuildResourceSummary(JsonElement resource)
    {
        var summary = new StringBuilder();
        
        var id = resource.GetProperty("id").GetString();
        summary.AppendLine($"# Resource: {id}");
        
        if (resource.TryGetProperty("type", out var type))
            summary.AppendLine($"Type: {type.GetString()}");
        
        if (resource.TryGetProperty("name", out var name))
            summary.AppendLine($"Name: {name.GetString()}");
        
        if (resource.TryGetProperty("description", out var desc))
            summary.AppendLine($"Description: {desc.GetString()}");
        
        if (resource.TryGetProperty("platformRef", out var platform))
            summary.AppendLine($"Platform: {platform.GetString()}");
        
        if (resource.TryGetProperty("ownerRef", out var owner))
            summary.AppendLine($"Owner: {owner.GetString()}");
        
        return summary.ToString();
    }
}
