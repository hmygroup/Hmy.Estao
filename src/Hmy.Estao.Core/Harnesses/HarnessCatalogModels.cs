using System.Text.Json.Serialization;

namespace Hmy.Estao.Core.Harnesses;

public static class HarnessCatalogItemTypes
{
    public const string Collection = "collection";
    public const string Snapshot = "snapshot";
    public static readonly string[] All = [.. HarnessFeatureIds.All, Collection, Snapshot];
}

public static class HarnessArtifactStates
{
    public const string Published = "published";
    public const string Deprecated = "deprecated";
    public const string Archived = "archived";
    public static readonly string[] All = [Published, Deprecated, Archived];
}

public static class HarnessCompatibilityStates
{
    public const string Native = "native";
    public const string Converted = "converted";
    public const string Partial = "partial";
    public const string Unsupported = "unsupported";
}

public sealed class HarnessCatalogManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("capabilityDescription")]
    public string CapabilityDescription { get; set; } = string.Empty;
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("ownerId")]
    public string OwnerId { get; set; } = string.Empty;
    [JsonPropertyName("ownerName")]
    public string OwnerName { get; set; } = string.Empty;
    [JsonPropertyName("team")]
    public string Team { get; set; } = "company";
    [JsonPropertyName("state")]
    public string State { get; set; } = HarnessArtifactStates.Published;
    [JsonPropertyName("sourceHarness")]
    public string SourceHarness { get; set; } = string.Empty;
    [JsonPropertyName("allowedScopes")]
    public List<string> AllowedScopes { get; set; } = ["personal", "project"];
    [JsonPropertyName("compatibility")]
    public Dictionary<string, string> Compatibility { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];
    [JsonPropertyName("changeNotes")]
    public string ChangeNotes { get; set; } = string.Empty;
    [JsonPropertyName("recommended")]
    public bool Recommended { get; set; }
    [JsonPropertyName("publishedUtc")]
    public DateTimeOffset PublishedUtc { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("references")]
    public List<HarnessArtifactReference> References { get; set; } = [];
}

public class HarnessArtifactReference
{
    [JsonPropertyName("repositoryId")]
    public string RepositoryId { get; set; } = string.Empty;
    [JsonPropertyName("artifactId")]
    public string ArtifactId { get; set; } = string.Empty;
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed record HarnessCatalogEntry(string RepositoryId, string RepositoryName,
    HarnessCatalogManifest Manifest, string PackagePath, long Size);

public sealed record HarnessCatalogDraft(string Id, string Name, string Summary, string Description,
    string Version, string Team, string ChangeNotes, IReadOnlyList<string> Tags,
    IReadOnlyList<string> AllowedScopes);

public sealed record HarnessPublishCandidate(string Key, string Name, string Feature,
    IReadOnlyList<HarnessArtifactPreview> Files);

public sealed class HarnessEnvironmentDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("harnessId")]
    public string HarnessId { get; set; } = string.Empty;
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "personal";
    [JsonPropertyName("rootPath")]
    public string RootPath { get; set; } = string.Empty;
    [JsonPropertyName("artifacts")]
    public List<HarnessEnvironmentArtifact> Artifacts { get; set; } = [];
    [JsonPropertyName("updatedUtc")]
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class HarnessEnvironmentArtifact : HarnessArtifactReference
{
    [JsonPropertyName("updateChannel")]
    public string UpdateChannel { get; set; } = "stable";
    [JsonPropertyName("appliedSha256")]
    public string AppliedSha256 { get; set; } = string.Empty;

    [JsonPropertyName("managedFiles")]
    public List<HarnessManagedFileState> ManagedFiles { get; set; } = [];
}

public sealed class HarnessManagedFileState
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("semanticSha256")]
    public string SemanticSha256 { get; set; } = string.Empty;
}

public sealed record HarnessDriftItem(string Path, string State, string ExpectedSha256, string ActualSha256);

public sealed record HarnessSyncPlanItem(HarnessCatalogEntry Entry, bool Enabled = true);

public sealed record HarnessSyncResult(IReadOnlyList<string> InstalledFiles,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> SkippedArtifacts,
    IReadOnlyList<string> RestoreDirectories);
