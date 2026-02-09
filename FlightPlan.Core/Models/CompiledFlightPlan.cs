using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlightPlan.Models;

#nullable enable

// ================================================================
// DIRECTION ENUM
// ================================================================

public enum Direction
{
    From,
    To
}

// ================================================================
// ROOT DOCUMENT
// ================================================================

public sealed class CompiledFlightPlan
{
    [JsonPropertyName("metadata")]
    public Metadata Metadata { get; init; } = default!;

    [JsonPropertyName("application")]
    public Application Application { get; init; } = default!;

    [JsonPropertyName("deliveryModel")]
    public DeliveryModel? DeliveryModel { get; init; }

    [JsonPropertyName("entities")]
    public EntityCollection Entities { get; init; } = default!;

    [JsonPropertyName("interfaces")]
    public List<ServiceExport> Interfaces { get; init; } = [];

    [JsonPropertyName("dependencies")]
    public List<Dependency> Dependencies { get; init; } = [];

    [JsonPropertyName("annotations")]
    public Dictionary<string, object>? Annotations { get; init; }

    /// <summary>
    /// Default toolchain for the application lifecycle (repo, work-tracking, build, deploy, etc).
    /// Values are tooling IDs (e.g., "github-repos") resolved from tooling:// refs in YAML.
    /// </summary>
    [JsonPropertyName("toolchain")]
    public Dictionary<string, string>? Toolchain { get; init; }

    [JsonPropertyName("terms")]
    public Dictionary<string, object>? Terms { get; init; }

    public PlatformEntity? GetPlatformById(string id)
    {
        return Entities.Platforms.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>
    /// Resolves an entity by ID based on the dependency kind and direction.
    /// </summary>
    /// <param name="dependency">The dependency being resolved</param>
    /// <param name="direction">The direction of the dependency</param>
    /// <returns>The entity if found, null otherwise</returns>
    public BaseEntity? Resolve(Dependency dependency, Direction direction)
    {
        string id = direction == Direction.From ? dependency.From : dependency.To;
        return dependency.Kind switch
        {
            "service-to-service" when direction == Direction.To => ResolveService(id),
            "service-to-service" when direction == Direction.From => Entities.Services.FirstOrDefault(s => s.Id == id),
            "service-to-resource" when direction == Direction.From => Entities.Services.FirstOrDefault(s => s.Id == id),
            "service-to-resource" when direction == Direction.To => Entities.Resources.FirstOrDefault(r => r.Id == id),
            _ => null
        };
    }

    private BaseEntity? ResolveService(string id)
    {
        var parts = id.Split('/');
        if (parts.Length != 2) return null;
        string serviceId = parts[0];
        string exportId = parts[1];
        return Entities.Services.FirstOrDefault(s => s.ExportIds.Contains(id));
    }
}

// ================================================================
// METADATA
// ================================================================

public sealed class Metadata
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = default!;

    [JsonPropertyName("compiledAt")]
    public DateTimeOffset CompiledAt { get; init; }

    [JsonPropertyName("sourceFiles")]
    public List<string> SourceFiles { get; init; } = [];
}

// ================================================================
// APPLICATION
// ================================================================

public sealed class Application
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = default!;

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("domain")]
    public string? Domain { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

// ================================================================
// DELIVERY MODEL
// ================================================================

public sealed class DeliveryModel
{
    [JsonPropertyName("hosting")]
    public string? Hosting { get; init; }

    [JsonPropertyName("serviceModel")]
    public string? ServiceModel { get; init; }

    [JsonPropertyName("tenancy")]
    public string? Tenancy { get; init; }

    [JsonPropertyName("primaryUsers")]
    public List<string>? PrimaryUsers { get; init; }
}

// ================================================================
// ENTITY COLLECTION
// ================================================================

public sealed class EntityCollection
{
    [JsonPropertyName("environments")]
    public List<EnvironmentEntity> Environments { get; init; } = [];

    [JsonPropertyName("zones")]
    public List<ZoneEntity> Zones { get; init; } = [];

    [JsonPropertyName("teams")]
    public List<TeamEntity> Teams { get; init; } = [];

    [JsonPropertyName("platforms")]
    public List<PlatformEntity> Platforms { get; init; } = [];

    [JsonPropertyName("dataClasses")]
    public List<DataClassEntity> DataClasses { get; set; } = [];

    [JsonPropertyName("resources")]
    public List<ResourceEntity> Resources { get; init; } = [];

    [JsonPropertyName("services")]
    public List<ServiceEntity> Services { get; init; } = [];

    [JsonPropertyName("tooling")]
    public List<ToolingEntity> Tooling { get; init; } = [];
}

// ================================================================
// BASE ENTITY (WIRE CONTRACT)
// ================================================================

[JsonConverter(typeof(EntityJsonConverter))]
public abstract class BaseEntity
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    /// <summary>
    /// Discriminator (service, resource, zone, platform, etc)
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = default!;

    [JsonPropertyName("name")]
    public string Name { get; init; } = default!;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("ownerRef")]
    public string? OwnerRef { get; init; }

    [JsonPropertyName("platformRef")]
    public string? PlatformRef { get; init; }

    [JsonPropertyName("dataClassRefs")]
    public List<string>? DataClassRefs { get; init; }

    /// <summary>
    /// External identifiers (e.g., integration URLs) that refer to this entity in other systems.
    /// </summary>
    [JsonPropertyName("foreignRefs")]
    public List<string>? ForeignRefs { get; init; }

    [JsonPropertyName("annotations")]
    public Dictionary<string, object>? Annotations { get; init; }
}

// ================================================================
// RESOURCE / SERVICE SUBCLASSES
// ================================================================

public class ResourceOrServiceEntity : BaseEntity
{
    [JsonPropertyName("zoneRef")]
    public string? ZoneRef { get; init; }
}

public sealed class ServiceEntity : ResourceOrServiceEntity
{
    /// <summary>
    /// Optional source control repository URL for this service.
    /// </summary>
    [JsonPropertyName("repoUrl")]
    public string? RepoUrl { get; init; }

    /// <summary>
    /// Service-specific overrides for the default toolchain.
    /// Values are tooling IDs resolved from tooling:// refs in YAML.
    /// </summary>
    [JsonPropertyName("toolchain")]
    public Dictionary<string, string>? Toolchain { get; init; }

    /// <summary>
    /// IDs of exports defined by this service
    /// (derived during compilation)
    /// </summary>
    [JsonPropertyName("exportIds")]
    public List<string> ExportIds { get; init; } = [];
}

public sealed class ResourceEntity : ResourceOrServiceEntity
{
    /// <summary>
    /// Resource kind (database, queue, external-service, etc)
    /// </summary>
    [JsonPropertyName("kind")]
    public string? ResourceKind { get; init; }
}
// ================================================================
// OTHER STRONGLY TYPED ENTITY SUBCLASSES
// ================================================================

public sealed class ZoneEntity : BaseEntity
{
    [JsonPropertyName("trust")]
    public string? Trust { get; init; }

    [JsonPropertyName("risks")]
    public List<string> Risks { get; init; } = [];
}

public sealed class PlatformEntity : BaseEntity
{
    [JsonPropertyName("category")]
    public string? Category { get; init; }
}

public sealed class TeamEntity : BaseEntity
{
}

public sealed class ToolingEntity : ResourceOrServiceEntity
{
    /// <summary>
    /// Tooling category/kind (source-control, ci-cd, observability, work-tracking, etc)
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("organization")]
    public string? Organization { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed class DataClassEntity : BaseEntity
{
}

public class EnvironmentEntity : BaseEntity
{
    [JsonPropertyName("promotesTo")]
    public List<string>? PromotesTo { get; init; }
}

// Fallback for unknown / future entity types
public sealed class GenericEntity : BaseEntity
{
}

// ================================================================
// POLYMORPHIC ENTITY JSON CONVERTER
// ================================================================

public sealed class EntityJsonConverter : JsonConverter<BaseEntity>
{
    public override BaseEntity Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
        {
            throw new JsonException("Entity is missing required 'type' property.");
        }

        var type = typeProp.GetString();

        Type targetType = type switch
        {
            "service"     => typeof(ServiceEntity),
            "resource"    => typeof(ResourceEntity),
            "zone"        => typeof(ZoneEntity),
            "platform"    => typeof(PlatformEntity),
            "team"        => typeof(TeamEntity),
            "tooling"     => typeof(ToolingEntity),
            "dataClass"   => typeof(DataClassEntity),
            "environment" => typeof(EnvironmentEntity),
            _             => typeof(GenericEntity)
        };

        return (BaseEntity)JsonSerializer.Deserialize(
            root.GetRawText(),
            targetType,
            options
        )!;
    }

    public override void Write(
        Utf8JsonWriter writer,
        BaseEntity value,
        JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
    }
}

// ================================================================
// SERVICE EXPORT (INTERFACE)
// ================================================================

public sealed class ServiceExport
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = default!;

    [JsonPropertyName("serviceRef")]
    public string ServiceRef { get; init; } = default!;

    [JsonPropertyName("name")]
    public string Name { get; init; } = default!;

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; } = default!;

    [JsonPropertyName("auth")]
    public string? Auth { get; init; }

    [JsonPropertyName("visibility")]
    public string? Visibility { get; init; }

    [JsonPropertyName("dataClassRefs")]
    public List<string>? DataClassRefs { get; init; }

    /// <summary>
    /// External consumers of this export (e.g., external://okta/...)
    /// </summary>
    [JsonPropertyName("consumers")]
    public List<string>? Consumers { get; init; }
}

// ================================================================
// DEPENDENCY EDGE
// ================================================================

public sealed class Dependency
{
    [JsonPropertyName("from")]
    public string From { get; init; } = default!;

    [JsonPropertyName("to")]
    public string To { get; init; } = default!;

    /// <summary>
    /// service->service, service->resource, etc
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = default!;

    [JsonPropertyName("access")]
    public string? Access { get; init; }

    /// <summary>
    /// When this dependency is derived (e.g., from exports.*.forwardsTo), this captures
    /// the specific export on the source service that declared it (service/export).
    /// </summary>
    [JsonPropertyName("fromExportId")]
    public string? FromExportId { get; init; }

    /// <summary>
    /// Provenance of this dependency edge (e.g., "forwardsTo").
    /// </summary>
    [JsonPropertyName("derivedFrom")]
    public string? DerivedFrom { get; init; }
}

// ================================================================
// JSON OPTIONS HELPER (OPTIONAL)
// ================================================================

public static class CompiledFlightPlanJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // EntityJsonConverter is attached via attribute,
        // but keeping this explicit allows override if needed.
        options.Converters.Add(new EntityJsonConverter());

        return options;
    }
}
