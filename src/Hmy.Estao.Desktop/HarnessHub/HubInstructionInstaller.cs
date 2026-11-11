using System.Text;
using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Desktop.HarnessHub;

internal static class HubInstructionInstaller
{
    public static string Destination(HarnessAdapter adapter) => Path.Combine(adapter.Profile, adapter.Id switch
    {
        "codex" => File.Exists(Path.Combine(adapter.Profile, "AGENTS.override.md")) &&
            new FileInfo(Path.Combine(adapter.Profile, "AGENTS.override.md")).Length > 0 ? "AGENTS.override.md" : "AGENTS.md",
        "claude" => "CLAUDE.md",
        "copilot" => "copilot-instructions.md",
        "opencode" => "AGENTS.md",
        _ => throw new ArgumentException("Unknown instruction target.")
    });

    public static async Task InstallAsync(HarnessAdapter adapter, string id, byte[] content, string dataDirectory, CancellationToken ct)
    {
        InstallerPaths.Relative(id);
        if (id.Contains('/')) throw new InvalidDataException("Invalid instruction identity.");
        var body = InstructionDocument.Read(content).Body.Trim();
        if (string.IsNullOrWhiteSpace(body) || body.Contains("<!-- estao-instruction:", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid or empty instruction body.");
        // Coordinate with the skill installer's writes and recovery in other Hub windows.
        var lockDirectory = Path.Combine(dataDirectory, "harness-installer");
        InstallerPaths.EnsurePrivateDirectory(lockDirectory);
        using var gate = new FileStream(InstallerPaths.Within(lockDirectory, "installer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (await new HarnessInstallationService(dataDirectory).GetInstallationStatusAsync(adapter) == HubOperationState.RecoveryRequired)
            throw new InvalidOperationException("Recover the interrupted installation first.").WithUserMessage("hub.recovery_required");
        var destination = Destination(adapter);
        var beforeHash = InstallerPaths.FileHash(destination);
        var before = beforeHash is null ? [] : await File.ReadAllBytesAsync(destination, ct);
        if (beforeHash is not null && InstallerPaths.Hash(before) != beforeHash)
            throw new IOException("Instructions changed while reading.").WithUserMessage("hub.review_changed");
        var existing = new UTF8Encoding(false, true).GetString(before);
        var start = $"<!-- estao-instruction:{id}:start -->";
        var end = $"<!-- estao-instruction:{id}:end -->";
        var block = start + "\n" + body + "\n" + end;
        var normalized = existing.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Contains(start, StringComparison.Ordinal) || normalized.Contains(end, StringComparison.Ordinal))
        {
            var first = normalized.IndexOf(start, StringComparison.Ordinal);
            if (first >= 0 && normalized.IndexOf(start, first + start.Length, StringComparison.Ordinal) < 0 &&
                normalized.IndexOf(end, StringComparison.Ordinal) == first + block.Length - end.Length &&
                normalized.AsSpan(first).StartsWith(block, StringComparison.Ordinal) &&
                normalized.IndexOf(end, first + block.Length, StringComparison.Ordinal) < 0) return;
            // Changed catalog content and local edits both require manual resolution;
            // never replace a block without an approved previous-content baseline.
            throw new IOException("An instruction block with this ID already exists with different content. Existing instructions were preserved.")
                .WithUserMessage("hub.instruction_conflict");
        }
        var separator = existing.Length == 0 || existing.EndsWith("\n\n", StringComparison.Ordinal) ? "" : existing.EndsWith('\n') ? "\n" : "\n\n";
        var after = Encoding.UTF8.GetBytes(existing + separator + block + "\n");
        if (beforeHash is not null)
        {
            var backup = Path.Combine(dataDirectory, "instruction-backups", Guid.NewGuid().ToString("N"));
            InstallerPaths.EnsurePrivateDirectory(backup);
            await File.WriteAllBytesAsync(Path.Combine(backup, adapter.Id + ".md"), before, ct);
        }
        InstallerPaths.CheckLocal(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await HubFileExporter.WriteAsync(destination, after, beforeHash, ct);
    }
}
