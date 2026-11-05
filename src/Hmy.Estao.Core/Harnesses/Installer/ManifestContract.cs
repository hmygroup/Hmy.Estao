using System.Reflection;
using System.Text.RegularExpressions;

namespace Hmy.Estao.Core.Harnesses.Installer;

public static partial class ManifestContract
{
    public static SourceManifest Bootstrap()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Hmy.Estao.Core.Harnesses.Installer.estao-harness.json")!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return InstallerJson.Read<SourceManifest>(memory.ToArray());
    }

    public static void Validate(SourceManifest manifest, string commit, string tree, IReadOnlyList<SourceEntry> entries)
    {
        if (manifest.Artifacts is null || manifest.Exclusions is null || manifest.Repository is null || manifest.Version is null || manifest.Commit is null || manifest.Tree is null ||
            manifest.Artifacts.Any(a => a is null || a.Id is null || a.Source is null || a.Harnesses is null || a.Requirements is null || a.Dependencies is null || a.MinimumVersions is null) ||
            manifest.Exclusions.Any(e => e is null || e.Source is null)) throw new InvalidDataException("Invalid null manifest fields.");
        if (manifest.SchemaVersion != 1 || manifest.Repository != "hmygroup/hmy-prompt-engineer" ||
            string.IsNullOrWhiteSpace(manifest.Version) || manifest.Artifacts.Count > 200 || manifest.Exclusions.Count > 200)
            throw new InvalidDataException("Unsupported source manifest.");
        if ((manifest.Commit.Length > 0 && manifest.Commit != commit) || (manifest.Tree.Length > 0 && manifest.Tree != tree))
            throw new InvalidDataException("The bundled compatibility contract does not cover this source revision. Publish estao-harness.json or update Estao's reviewed bootstrap.");
        if (entries.Count > 5000 || entries.Sum(e => e.Size) > 50L * 1024 * 1024)
            throw new InvalidDataException("Source snapshot exceeds safety limits.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            InstallerPaths.Relative(entry.Path);
            if (!paths.Add(entry.Path) || entry.Mode is not ("100644" or "100755") || entry.Size is < 0 or > 4 * 1024 * 1024 || !Sha().IsMatch(entry.Sha))
                throw new InvalidDataException("Unsafe, linked, duplicate or oversized source entry.");
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in manifest.Artifacts)
        {
            InstallerPaths.Relative(artifact.Source);
            if (!Id().IsMatch(artifact.Id) || !ids.Add(artifact.Id) || artifact.Harnesses.Count == 0 ||
                artifact.Harnesses.Any(h => !HarnessAdapter.Ids.Contains(h)) ||
                artifact.Classification is not ("shared" or "harness-specific") ||
                artifact.Transformation != "native" || artifact.Category is not ("skill" or "agent" or "command" or "mcp") ||
                artifact.Destination != artifact.Category || artifact.Dependencies.Count > 50 ||
                artifact.MinimumVersions.Any(p => !HarnessAdapter.Ids.Contains(p.Key) || !Version.TryParse(p.Value, out _)))
                throw new InvalidDataException("Unsupported artifact mapping or transformation.");
            if (!entries.Any(e => Matches(artifact.Source, e.Path))) throw new InvalidDataException($"Missing declared artifact: {artifact.Id}.");
        }
        foreach (var artifact in manifest.Artifacts)
            if (artifact.Dependencies.Any(d => !ids.Contains(d) || d == artifact.Id))
                throw new InvalidDataException("Invalid artifact dependency.");
        foreach (var exclusion in manifest.Exclusions)
        {
            InstallerPaths.Relative(exclusion.Source);
            if (exclusion.Classification is not ("documentation" or "support" or "unsupported" or "external-runtime") || string.IsNullOrWhiteSpace(exclusion.Reason))
                throw new InvalidDataException("Invalid exclusion.");
        }
        foreach (var entry in entries.Where(e => e.Path != "estao-harness.json"))
            if (manifest.Artifacts.Count(a => Matches(a.Source, entry.Path)) + manifest.Exclusions.Count(e => Matches(e.Source, entry.Path)) != 1)
                throw new InvalidDataException($"Source coverage is missing or ambiguous: {entry.Path}.");
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        void Visit(SourceArtifact artifact)
        {
            if (visited.Contains(artifact.Id)) return;
            if (!visiting.Add(artifact.Id)) throw new InvalidDataException("Cyclic artifact dependency.");
            foreach (var dependency in artifact.Dependencies) Visit(manifest.Artifacts.Single(a => a.Id == dependency));
            visiting.Remove(artifact.Id);
            visited.Add(artifact.Id);
        }
        foreach (var artifact in manifest.Artifacts) Visit(artifact);
    }

    public static bool Matches(string prefix, string path) => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex Id();
    [GeneratedRegex("^[a-f0-9]{40}$")]
    internal static partial Regex Sha();
}
