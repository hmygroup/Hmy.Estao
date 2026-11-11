using Hmy.Estao.Core.Harnesses.Installer;

namespace Hmy.Estao.Desktop.HarnessHub;

internal static class HubSkillSafety
{
    // Codex extracts built-ins to <CODEX_HOME>/skills/.system and stamps that
    // cache with .codex-system-skills.marker. Never infer ownership from names:
    // personal skills may legitimately override bundled skills in every tool.
    internal static bool IsProtectedDirectory(string directory) =>
        Path.GetFileName(directory).StartsWith('.') ||
        File.Exists(Path.Combine(directory, ".codex-system-skills.marker"));

    internal static bool IsProtectedWithin(string root, string directory)
    {
        for (var current = directory; ; current = Path.GetDirectoryName(current)!)
        {
            if (IsProtectedDirectory(current)) return true;
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
        }
    }

    internal static void ValidateUserSkill(HarnessAdapter adapter, string skillFile)
    {
        InstallerPaths.CheckLocal(skillFile);
        var file = Path.GetFullPath(skillFile);
        if (!Path.GetFileName(file).Equals("SKILL.md", StringComparison.Ordinal))
            throw new InvalidDataException("Only installed skill folders can be uninstalled.");
        var root = new[] { adapter.SkillsRoot }.Concat(adapter.CompatibleSkillsRoots)
            .Select(Path.GetFullPath).FirstOrDefault(root => file.StartsWith(
                Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (root is null || Path.GetDirectoryName(file)!.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The skill is outside a personal skill folder.");
        if (IsProtectedWithin(root, Path.GetDirectoryName(file)!))
            throw new InvalidDataException("System skills cannot be uninstalled.");
    }
}
