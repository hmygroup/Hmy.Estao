using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hmy.Estao.Core.Harnesses.Installer;

public static class InstallerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T Read<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Options)
        ?? throw new InvalidDataException("Empty installer document.");
}

public sealed class SourceManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Repository { get; set; } = "hmygroup/hmy-prompt-engineer";
    public string Version { get; set; } = "1";
    public string Commit { get; set; } = "";
    public string Tree { get; set; } = "";
    public List<SourceArtifact> Artifacts { get; set; } = [];
    public List<SourceExclusion> Exclusions { get; set; } = [];
}

public sealed class SourceArtifact
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string Category { get; set; } = "skill";
    public string Classification { get; set; } = "shared";
    public string Destination { get; set; } = "skill";
    public string Transformation { get; set; } = "native";
    public List<string> Harnesses { get; set; } = [];
    public List<string> Requirements { get; set; } = [];
    public List<string> Dependencies { get; set; } = [];
    public Dictionary<string, string> MinimumVersions { get; set; } = [];
}

public sealed record SourceExclusion(string Source, string Classification, string Reason);
public sealed record SourceEntry(string Path, string Sha, long Size, string Mode);
public sealed record CoverageItem(string Source, string Classification, string Reason, int FileCount);
public sealed record PreparedFile(string ArtifactId, string RelativePath, byte[] Content, string Format = "file");
public sealed record PreparedSnapshot(string Commit, string Tree, string ManifestVersion,
    IReadOnlyList<PreparedFile> Files, IReadOnlyList<CoverageItem> Coverage,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Dependencies = null);

public enum InstallAction { Create, Update, Merge, AlreadyPresent, Conflict }
public sealed record PlannedFile(string ArtifactId, string Path, byte[] Content, string? BeforeHash,
    string AfterHash, InstallAction Action, string Reason, IReadOnlyDictionary<string, string>? StructuredNodes = null);
public sealed record InstallPlan(string HarnessId, string Profile, string Commit, string ManifestVersion,
    IReadOnlyList<PlannedFile> Files, IReadOnlyList<CoverageItem> Coverage, string? StateBeforeHash = null);
public sealed record InstallResult(int Created, int Updated, int Unchanged, int Conflicts, string Commit);

public sealed class InstallerState
{
    public int SchemaVersion { get; set; } = 1;
    public string Repository { get; set; } = "hmygroup/hmy-prompt-engineer";
    public string HarnessId { get; set; } = "";
    public string Profile { get; set; } = "";
    public string SourceCommit { get; set; } = "";
    public string AdapterVersion { get; set; } = HarnessAdapter.AdapterVersion;
    public bool HasConflicts { get; set; }
    public List<InstalledFile> Files { get; set; } = [];
}

public sealed record InstalledFile(string ArtifactId, string Path, string Hash, bool Managed = true,
    IReadOnlyDictionary<string, string>? StructuredNodes = null);
public sealed class TransactionJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string HarnessId { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Status { get; set; } = "prepared";
    public List<JournalEntry> Entries { get; set; } = [];
}

public sealed record JournalEntry(string Path, string? BeforeHash, string AfterHash, string BackupFile);

public enum HubOperationState
{
    NotConnected, NoRepositoryAccess, NotDetected, Ready, Downloading, Planning,
    Installing, Installed, UpdateAvailable, PartiallyInstalled, Conflict, Error, RecoveryRequired
}

public sealed class HubOperation
{
    public HubOperationState State { get; private set; } = HubOperationState.NotConnected;
    public bool IsBusy => State is HubOperationState.Downloading or HubOperationState.Planning or HubOperationState.Installing;
    public void Begin()
    {
        if (IsBusy) throw new InvalidOperationException("An operation is already running.");
        State = HubOperationState.Downloading;
    }
    public void Set(HubOperationState state) => State = state;
}
