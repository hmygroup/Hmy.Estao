using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Desktop.HarnessHub;

internal static class HubSkillUninstaller
{
    public static void Uninstall(HubSkill skill, Func<string, HarnessAdapter> adapterFactory,
        string dataDirectory, CancellationToken ct)
    {
        if (skill.ItemType != "skill" || skill.Occurrences.Count == 0)
            throw new InvalidOperationException("Select an installed skill.").WithUserMessage("hub.select_skill");

        // Validate every occurrence before moving anything, including duplicated
        // observations of a shared physical folder. JS never supplies filesystem paths.
        foreach (var occurrence in skill.Occurrences)
        {
            ct.ThrowIfCancellationRequested();
            HubSkillSafety.ValidateUserSkill(adapterFactory(occurrence.ToolId), occurrence.Path);
            if (occurrence.Hash is null || InstallerPaths.FileHash(occurrence.Path) != occurrence.Hash)
                throw new IOException("The skill changed. Refresh before uninstalling.").WithUserMessage("hub.review_changed");
        }
        var directories = skill.Occurrences.Select(item => Path.GetDirectoryName(Path.GetFullPath(item.Path))!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var directory in directories) ValidateTree(directory, ct);

        var backupRoot = Path.Combine(dataDirectory, "uninstalled-skills", Guid.NewGuid().ToString("N"));
        if (directories.Any(directory => backupRoot.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The backup location must be outside the skill being removed.");
        InstallerPaths.EnsurePrivateDirectory(backupRoot);
        var moved = new List<(string Original, string Backup)>();
        try
        {
            foreach (var directory in directories)
            {
                ct.ThrowIfCancellationRequested();
                var backup = Path.Combine(backupRoot, moved.Count.ToString(), Path.GetFileName(directory));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                Directory.Move(directory, backup);
                moved.Add((directory, backup));
            }
        }
        catch
        {
            // Never overwrite an external edit during rollback. Backups are kept
            // even if restoring a folder fails, so no skill data is discarded.
            foreach (var (original, backup) in moved.AsEnumerable().Reverse())
                if (!Directory.Exists(original) && !File.Exists(original)) Directory.Move(backup, original);
            throw;
        }
    }

    private static void ValidateTree(string root, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            InstallerPaths.CheckLocal(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > 20000) throw new IOException("Skill folder exceeds the safe uninstall limit.");
                InstallerPaths.CheckLocal(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
    }
}
