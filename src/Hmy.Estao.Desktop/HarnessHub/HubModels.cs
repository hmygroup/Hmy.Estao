using Shofia.WebHost.Messaging;

namespace Hmy.Estao.Desktop.HarnessHub;

internal static class HubContracts
{
    public static readonly RequestType<EmptyRequest, HubState> State = new("estao.hub.state");
    public static readonly RequestType<HubCommand, EmptyRequest> Command = new("estao.hub.command");
}

internal sealed record HubCommand(string Action, string? HarnessId = null, string? ReviewId = null,
    Dictionary<string, string[]>? Selection = null);
internal sealed record HubTool(string Id, string Name, bool Detected, string Status, string? Error = null);
internal sealed record HubArtifact(string Id, string Description, string Category, string Status,
    int FileCount, string Source, IReadOnlyList<string> Requirements, IReadOnlyList<string> Dependencies,
    IReadOnlyList<HubFile> Files);
internal sealed record HubFile(string Path, string Action, string Reason);
internal sealed record HubToolReview(string Id, string Name, string Destination, IReadOnlyList<HubArtifact> Artifacts,
    IReadOnlyList<HubExclusion> Exclusions);
internal sealed record HubExclusion(string Source, string Classification, string Reason, int FileCount);
internal sealed record HubReview(string Id, string Commit, IReadOnlyList<HubToolReview> Tools);
internal sealed record HubResult(string Id, string Name, string Outcome, int Created, int Updated, int Unchanged,
    int Conflicts, string? Error = null);
internal sealed record HubDeviceCode(string Code, string VerificationUrl);
internal sealed record HubState(bool Busy, string Phase, string Progress, bool Connected, string Connection,
    IReadOnlyList<HubTool> Tools, HubReview? Review, IReadOnlyList<HubResult> Results, HubDeviceCode? DeviceCode,
    string? Error);
