using Hmy.Estao.Core.Harnesses.Installer;

namespace Hmy.Estao.Desktop.HarnessHub;

internal static class HubReviewBuilder
{
    public static HubToolReview Build(HarnessAdapter adapter, PreparedSnapshot snapshot, InstallPlan plan)
    {
        var metadata = (snapshot.Artifacts ?? []).ToDictionary(a => a.Id);
        var artifacts = plan.Files.GroupBy(f => f.ArtifactId).Select(group =>
        {
            metadata.TryGetValue(group.Key, out var info);
            var files = group.ToArray();
            var status = files.Any(f => f.Action == InstallAction.Conflict) ? "conflict"
                : files.Any(f => f.Action is InstallAction.Update or InstallAction.Merge) ? "update"
                : files.Any(f => f.Action == InstallAction.Create) ? "new" : "present";
            return new HubArtifact(group.Key, info?.Description ?? "Shared configuration", info?.Category ?? "skill",
                status, files.Length, info?.Source ?? group.Key, info?.Requirements ?? [],
                snapshot.Dependencies?.GetValueOrDefault(group.Key) ?? [],
                files.Select(f => new HubFile(f.Path, f.Action.ToString(), f.Reason)).ToArray());
        }).OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(adapter.Id, adapter.DisplayName, adapter.SkillsRoot, artifacts,
            snapshot.Coverage.Where(c => c.Classification != "installable")
                .Select(c => new HubExclusion(c.Source, c.Classification, c.Reason, c.FileCount)).ToArray());
    }

    public static PreparedSnapshot Select(PreparedSnapshot snapshot, IEnumerable<string> selection)
    {
        var chosen = selection.ToHashSet(StringComparer.Ordinal);
        var available = snapshot.Files.Select(f => f.ArtifactId).ToHashSet(StringComparer.Ordinal);
        if (chosen.Any(id => !available.Contains(id))) throw new ArgumentException("The selection contains an unknown artifact. Review again.");
        foreach (var id in chosen)
            if (snapshot.Dependencies?.GetValueOrDefault(id)?.Any(dependency => !chosen.Contains(dependency)) == true)
                throw new ArgumentException($"Select the required dependencies for '{id}' or deselect it.");
        return snapshot with
        {
            Files = snapshot.Files.Where(f => chosen.Contains(f.ArtifactId)).ToArray(),
            Dependencies = snapshot.Dependencies?.Where(d => chosen.Contains(d.Key)).ToDictionary(d => d.Key, d => d.Value),
            Artifacts = snapshot.Artifacts?.Where(a => chosen.Contains(a.Id)).ToArray()
        };
    }
}
