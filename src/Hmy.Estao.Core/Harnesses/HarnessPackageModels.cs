using System.Text.Json.Serialization;

namespace Hmy.Estao.Core.Harnesses;

public sealed class HarnessPackageManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("packageVersion")]
    public string PackageVersion { get; set; } = "1.0.0";

    [JsonPropertyName("sourceHarness")]
    public string SourceHarness { get; set; } = string.Empty;

    [JsonPropertyName("sourceScope")]
    public string SourceScope { get; set; } = "personal";

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("publishedUtc")]
    public DateTimeOffset PublishedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("artifacts")]
    public List<HarnessPackageArtifact> Artifacts { get; set; } = [];
}

public sealed class HarnessPackageArtifact
{
    [JsonPropertyName("feature")]
    public string Feature { get; set; } = string.Empty;

    [JsonPropertyName("logicalPath")]
    public string LogicalPath { get; set; } = string.Empty;

    [JsonPropertyName("archivePath")]
    public string ArchivePath { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("redacted")]
    public bool Redacted { get; set; }
}

public sealed record HarnessPackageDraft(
    string Id,
    string Name,
    string Description,
    string PackageVersion,
    string Author);

public sealed record HarnessArtifactPreview(
    string Feature,
    string LogicalPath,
    long Size,
    bool Redacted);

public sealed record HarnessHubPackage(
    HarnessPackageManifest Manifest,
    string Path,
    long Size,
    DateTimeOffset LastWriteUtc);

public sealed record HarnessInstallResult(
    IReadOnlyList<string> InstalledFiles,
    IReadOnlyList<string> SkippedArtifacts,
    IReadOnlyList<string> Warnings,
    string? BackupDirectory);

public sealed class HarnessRestoreManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("harnessId")]
    public string HarnessId { get; set; } = string.Empty;

    [JsonPropertyName("packageId")]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyName("packageName")]
    public string PackageName { get; set; } = string.Empty;

    [JsonPropertyName("packageVersion")]
    public string PackageVersion { get; set; } = string.Empty;

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("entries")]
    public List<HarnessRestoreEntry> Entries { get; set; } = [];
}

public sealed class HarnessRestoreEntry
{
    [JsonPropertyName("targetPath")]
    public string TargetPath { get; set; } = string.Empty;

    [JsonPropertyName("existed")]
    public bool Existed { get; set; }
}

public sealed record HarnessRestorePoint(
    HarnessRestoreManifest Manifest,
    string Directory,
    bool IsComplete = true);

public sealed record HarnessRestoreResult(
    IReadOnlyList<string> RestoredFiles,
    IReadOnlyList<string> RemovedFiles);

internal sealed record HarnessArtifactSource(
    string Feature,
    string LogicalPath,
    byte[] Content,
    bool Redacted = false);

public sealed class PortableMcpConfiguration
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, PortableMcpServer> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PortableMcpServer
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "stdio";

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = [];

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("tools")]
    public List<string> Tools { get; set; } = [];
}
