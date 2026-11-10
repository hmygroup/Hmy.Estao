using Shofia.WebHost.Messaging;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Desktop.HarnessHub;

internal static class HubContracts
{
    public static readonly RequestType<EmptyRequest, HubState> State = new("estao.hub.state");
    public static readonly RequestType<HubCommand, EmptyRequest> Command = new("estao.hub.command");
}

internal sealed record HubCommand(string Action, string? HarnessId = null, string? ReviewId = null,
    Dictionary<string, string[]>? Selection = null, string? SkillId = null);
internal sealed record HubTool(string Id, string Name, bool Detected, string Status, string? Error = null, UserMessage? ErrorMessage = null);
internal sealed record HubArtifact(string Id, string Description, string Category, string Status,
    int FileCount, string Source, IReadOnlyList<string> Requirements, IReadOnlyList<string> Dependencies,
    IReadOnlyList<HubFile> Files);
internal sealed record HubFile(string Path, string Action, string Reason, UserMessage? ReasonMessage = null);
internal sealed record HubToolReview(string Id, string Name, string Destination, IReadOnlyList<HubArtifact> Artifacts,
    IReadOnlyList<HubExclusion> Exclusions);
internal sealed record HubExclusion(string Source, string Classification, string Reason, int FileCount);
internal sealed record HubReview(string Id, string Commit, IReadOnlyList<HubToolReview> Tools);
internal sealed record HubResult(string Id, string Name, string Outcome, int Created, int Updated, int Unchanged,
    int Conflicts, string? Error = null, UserMessage? ErrorMessage = null);
internal sealed record HubDeviceCode(string Code, string VerificationUrl);
internal sealed record HubState(bool Busy, string Phase, string Progress, bool Connected, string Connection,
    IReadOnlyList<HubTool> Tools, HubReview? Review, IReadOnlyList<HubResult> Results, HubDeviceCode? DeviceCode,
    string? Error, UserMessage? ProgressMessage = null, UserMessage? ConnectionMessage = null, UserMessage? ErrorMessage = null,
    bool AuthorizationChecked = false, bool CheckingAuthorization = false,
    IReadOnlyList<HubSkill>? Skills = null, string? LibraryCommit = null, string? LibraryError = null, bool LibraryLoaded = false,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? CatalogCategories = null,
    string CatalogDefaultLanguage = "en", string? CatalogError = null);
internal sealed record HubSkill(string Id, string Name, string Description, bool InLibrary, bool Installable,
    IReadOnlyList<string> CompatibleTools, IReadOnlyList<HubSkillOccurrence> Occurrences, string? Source = null, string? Issue = null,
    bool Curated = false, string ItemType = "skill", IReadOnlyDictionary<string, string>? Descriptions = null,
    IReadOnlyList<string>? Categories = null, string DefaultLanguage = "en");
internal sealed record HubSkillOccurrence(string ToolId, string Path, bool Shared, string? Hash, string? Error = null);
