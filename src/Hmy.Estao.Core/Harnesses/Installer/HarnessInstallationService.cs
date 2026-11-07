using System.Text.Json;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Core.Harnesses.Installer;

/// <summary>One additive writer. Transactions are recoverable, not filesystem-wide atomic.</summary>
public sealed class HarnessInstallationService
{
    private readonly string _directory;
    private readonly Action<string>? _faultPoint;

    public HarnessInstallationService(string dataDirectory, Action<string>? faultPoint = null)
    {
        _directory = Path.GetFullPath(Path.Combine(dataDirectory, "harness-installer"));
        InstallerPaths.CheckLocal(_directory);
        _faultPoint = faultPoint;
    }

    public Task<InstallPlan> PlanAsync(HarnessAdapter adapter, PreparedSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var statePath = StatePath(adapter);
        var stateBefore = InstallerPaths.FileHash(statePath);
        var state = LoadState(adapter);
        var planned = new List<PlannedFile>();
        foreach (var group in snapshot.Files.GroupBy(f => Path.GetFullPath(f.RelativePath), StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!adapter.IsAllowedDestination(group.Key)) throw new InvalidDataException("Adapter output escaped its allowed destinations.");
            var file = group.First();
            var hash = InstallerPaths.Hash(file.Content);
            var duplicateConflict = group.Any(f => InstallerPaths.Hash(f.Content) != hash || f.Format != file.Format);
            string? before = null;
            var action = InstallAction.Create;
            var content = file.Content;
            var reason = "Archivo nuevo.";
            UserMessage? reasonInfo = null;
            var destination = group.Key;
            try
            {
                var shadows = adapter.ShadowPaths(group.Key);
                if (shadows.Any(p => InstallerPaths.FileHash(p) != hash))
                    throw new InvalidDataException("Otra ubicación compatible contiene esta skill con contenido diferente o recursos incompletos.");
                if (shadows.Count > 0 && !System.IO.File.Exists(destination)) destination = shadows[0];
                before = InstallerPaths.FileHash(destination);
                var previous = state.Files.FirstOrDefault(f => f.Path.Equals(destination, StringComparison.OrdinalIgnoreCase));
                if (before == hash) { action = InstallAction.AlreadyPresent; reason = "Ya existe con contenido idéntico."; }
                else if (before is not null)
                {
                    if (file.Format != "file")
                    {
                        content = StructuredConfiguration.Merge(File.ReadAllBytes(group.Key), file.Content, file.Format);
                        hash = InstallerPaths.Hash(content);
                        action = hash == before ? InstallAction.AlreadyPresent : InstallAction.Merge;
                        reason = action == InstallAction.Merge ? "Añadir configuración ausente; conservar contenido local." : "Configuración equivalente.";
                    }
                    else if (previous is { Managed: true } && previous.Hash == before)
                    { action = InstallAction.Update; reason = "Actualizar archivo administrado sin modificaciones locales."; }
                    else { action = InstallAction.Conflict; reason = "Contenido local ajeno o modificado: se conserva."; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { action = InstallAction.Conflict; reason = "Destino o configuración no integrable de forma segura: " + ex.Message; reasonInfo = new("installer.reason.unsafe"); }
            if (duplicateConflict) { action = InstallAction.Conflict; reason = "Varios orígenes diferentes ocupan el mismo destino."; reasonInfo = new("installer.reason.duplicate"); }
            planned.Add(new PlannedFile(file.ArtifactId, destination, content.ToArray(), before, hash, action, reason,
                file.Format == "jsonc" && action != InstallAction.Conflict ? StructuredConfiguration.NodeHashes(file.Content) : null, reasonInfo));
        }
        // A skill/resource directory is one coherent group. No partial skills on a resource collision.
        var blocked = planned.Where(p => p.Action == InstallAction.Conflict).Select(p => p.ArtifactId).ToHashSet(StringComparer.Ordinal);
        if (snapshot.Dependencies is not null)
        {
            bool added;
            do
            {
                added = false;
                foreach (var item in snapshot.Dependencies)
                    if (item.Value.Any(blocked.Contains)) added |= blocked.Add(item.Key);
            } while (added);
        }
        planned = planned.Select(p => blocked.Contains(p.ArtifactId) && p.Action != InstallAction.Conflict
            ? p with { Action = InstallAction.Conflict, Reason = "Se conserva el grupo completo por un conflicto en sus recursos.", ReasonInfo = new("installer.reason.group_conflict") } : p).ToList();
        return Task.FromResult(new InstallPlan(adapter.Id, adapter.Profile, snapshot.Commit, snapshot.ManifestVersion,
            planned, snapshot.Coverage, stateBefore));
    }

    public async Task<InstallResult> ApplyAsync(HarnessAdapter adapter, InstallPlan plan, bool preserveConflicts,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (plan.HarnessId != adapter.Id || plan.Profile != adapter.Profile) throw new InvalidDataException("Installation profile changed.");
        if (!preserveConflicts && plan.Files.Any(f => f.Action == InstallAction.Conflict)) throw new InvalidOperationException("Review conflicts before installation.");
        EnsurePrivateDirectory();
        using var gate = AcquireLock();
        if (PendingDirectories(adapter).Any()) throw new InvalidOperationException("Recovery is required before another installation.");
        var statePath = StatePath(adapter);
        if (InstallerPaths.FileHash(statePath) != plan.StateBeforeHash) throw new IOException("Installer state changed. Prepare a new plan.");
        var writes = plan.Files.Where(f => f.Action is InstallAction.Create or InstallAction.Update or InstallAction.Merge).ToList();
        foreach (var file in plan.Files.Where(f => f.Action != InstallAction.Conflict))
        {
            ct.ThrowIfCancellationRequested();
            if (!(adapter.IsAllowedDestination(file.Path) || file.Action == InstallAction.AlreadyPresent && adapter.IsObservedDestination(file.Path)) || InstallerPaths.Hash(file.Content) != file.AfterHash)
                throw new InvalidDataException("Invalid prepared output.");
            if (InstallerPaths.FileHash(file.Path) != file.BeforeHash) throw new IOException("A destination changed after planning. Prepare a new plan.");
        }
        var state = LoadState(adapter);
        state.SourceCommit = plan.Commit;
        state.HasConflicts = plan.Files.Any(f => f.Action == InstallAction.Conflict);
        foreach (var file in plan.Files.Where(f => f.Action != InstallAction.Conflict))
        {
            var previous = state.Files.FirstOrDefault(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            var managed = file.Action != InstallAction.AlreadyPresent || previous is { Managed: true } && previous.Hash == file.BeforeHash;
            state.Files.RemoveAll(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            state.Files.Add(new InstalledFile(file.ArtifactId, file.Path, file.AfterHash, managed, file.StructuredNodes));
        }
        state.Files = state.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, InstallerJson.Options);
        if (writes.Count == 0 && InstallerPaths.Hash(stateBytes) == plan.StateBeforeHash) return Result(plan);
        var transactionDirectory = Path.Combine(_directory, "transactions", Guid.NewGuid().ToString("N"));
        InstallerPaths.CheckLocal(transactionDirectory);
        Directory.CreateDirectory(transactionDirectory);
        InstallerPaths.CheckLocal(transactionDirectory);
        var journal = new TransactionJournal { HarnessId = adapter.Id, Profile = adapter.Profile };
        var outputs = writes.Select(f => (f.Path, f.Content, f.BeforeHash, f.AfterHash)).ToList();
        outputs.Add((statePath, stateBytes, plan.StateBeforeHash, InstallerPaths.Hash(stateBytes)));
        var journalPath = Path.Combine(transactionDirectory, "journal.json");
        try
        {
            for (var index = 0; index < outputs.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var output = outputs[index];
                InstallerPaths.CheckLocal(output.Path);
                var backup = $"{index:D5}.before";
                if (output.BeforeHash is not null)
                {
                    var original = await File.ReadAllBytesAsync(output.Path, ct).ConfigureAwait(false);
                    if (InstallerPaths.Hash(original) != output.BeforeHash) throw new IOException("Destination changed during backup.");
                    WriteDurable(Path.Combine(transactionDirectory, backup), original);
                }
                WriteDurable(Path.Combine(transactionDirectory, $"{index:D5}.after"), output.Content);
                journal.Entries.Add(new(output.Path, output.BeforeHash, output.AfterHash, backup));
            }
            WriteDocument(journalPath, journal);
            journal.Status = "applying";
            WriteDocument(journalPath, journal);
            for (var index = 0; index < outputs.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var output = outputs[index];
                progress.ReportMessage(index == outputs.Count - 1 ? new("hub.saving_state") : new("hub.installing", index + 1, outputs.Count - 1),
                    index == outputs.Count - 1 ? "Guardando estado de instalación…" : $"Instalando {index + 1}/{outputs.Count - 1}…");
                _faultPoint?.Invoke(index == outputs.Count - 1 ? "before-state" : "before-file");
                if (InstallerPaths.FileHash(output.Path) != output.BeforeHash) throw new IOException("A destination changed before writing.");
                Replace(output.Path, output.Content, output.BeforeHash);
                _faultPoint?.Invoke("after-file");
            }
            journal.Status = "committed";
            WriteDocument(journalPath, journal);
            return Result(plan);
        }
        catch
        {
            // If preparation failed, no destination has been touched. Once applying, recover conditionally.
            if (journal.Status == "applying")
            {
                var conflicts = Rollback(adapter, transactionDirectory, journal);
                journal.Status = conflicts.Count == 0 ? "rolled-back" : "recovery-required";
                WriteDocument(journalPath, journal);
                if (conflicts.Count > 0) throw new IOException("La operación falló y hay cambios posteriores que requieren recuperación manual. No se han sobrescrito.");
            }
            throw;
        }
    }

    public Task<IReadOnlyList<string>> RecoverAsync(HarnessAdapter adapter, CancellationToken ct = default)
    {
        EnsurePrivateDirectory();
        using var gate = AcquireLock();
        var issues = new List<string>();
        foreach (var directory in PendingDirectories(adapter))
        {
            ct.ThrowIfCancellationRequested();
            var journalPath = Path.Combine(directory, "journal.json");
            var journal = ReadJournal(journalPath);
            var conflicts = Rollback(adapter, directory, journal);
            issues.AddRange(conflicts);
            journal.Status = conflicts.Count == 0 ? "rolled-back" : "recovery-required";
            WriteDocument(journalPath, journal);
        }
        return Task.FromResult<IReadOnlyList<string>>(issues);
    }

    public Task<HubOperationState> GetInstallationStatusAsync(HarnessAdapter adapter, string? sourceCommit = null)
    {
        if (PendingDirectories(adapter).Any()) return Task.FromResult(HubOperationState.RecoveryRequired);
        var state = LoadState(adapter);
        if (state.HasConflicts) return Task.FromResult(HubOperationState.PartiallyInstalled);
        if (state.Files.Count == 0) return Task.FromResult(HubOperationState.Ready);
        if (state.Files.Any(f => InstallerPaths.FileHash(f.Path) != f.Hash)) return Task.FromResult(HubOperationState.Conflict);
        return Task.FromResult(sourceCommit is not null && sourceCommit != state.SourceCommit ? HubOperationState.UpdateAvailable : HubOperationState.Installed);
    }

    private List<string> Rollback(HarnessAdapter adapter, string directory, TransactionJournal journal)
    {
        if (journal.Profile != adapter.Profile || journal.HarnessId != adapter.Id) throw new InvalidDataException("Invalid recovery profile.");
        var issues = new List<string>();
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            if (!adapter.IsAllowedDestination(entry.Path) && entry.Path != StatePath(adapter)) throw new InvalidDataException("Unsafe recovery destination.");
            try
            {
                var current = InstallerPaths.FileHash(entry.Path);
                if (current == entry.BeforeHash) continue;
                if (current != entry.AfterHash) { issues.Add(entry.Path); continue; }
                if (entry.BeforeHash is null)
                {
                    InstallerPaths.CheckLocal(entry.Path);
                    File.Delete(entry.Path);
                }
                else
                {
                    var backup = InstallerPaths.Within(directory, entry.BackupFile);
                    var bytes = File.ReadAllBytes(backup);
                    if (InstallerPaths.Hash(bytes) != entry.BeforeHash) throw new InvalidDataException("Corrupt recovery backup.");
                    Replace(entry.Path, bytes, entry.AfterHash);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { issues.Add(entry.Path); }
        }
        return issues;
    }

    private InstallerState LoadState(HarnessAdapter adapter)
    {
        var path = StatePath(adapter);
        if (InstallerPaths.FileHash(path) is null) return new() { HarnessId = adapter.Id, Profile = adapter.Profile };
        var state = InstallerJson.Read<InstallerState>(File.ReadAllBytes(path));
        if (state.SchemaVersion != 1 || state.Repository != GitHubRepositorySource.Repository || state.HarnessId != adapter.Id || state.Profile != adapter.Profile ||
            state.Files.Count > 5000 || state.Files.Any(f => !(f.Managed ? adapter.IsAllowedDestination(f.Path) : adapter.IsObservedDestination(f.Path))) ||
            state.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Files.Count)
            throw new InvalidDataException("Invalid installer state. Legacy state has not been changed.");
        return state;
    }

    private IEnumerable<string> PendingDirectories(HarnessAdapter adapter)
    {
        var root = Path.Combine(_directory, "transactions");
        InstallerPaths.CheckLocal(root);
        if (!Directory.Exists(root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            InstallerPaths.CheckLocal(directory);
            var path = Path.Combine(directory, "journal.json");
            if (InstallerPaths.FileHash(path) is null) continue;
            var journal = ReadJournal(path);
            if (journal.HarnessId == adapter.Id && journal.Profile == adapter.Profile && journal.Status is "applying" or "recovery-required") yield return directory;
        }
    }
    private static TransactionJournal ReadJournal(string path)
    {
        InstallerPaths.CheckLocal(path);
        var journal = InstallerJson.Read<TransactionJournal>(File.ReadAllBytes(path));
        if (journal.SchemaVersion != 1 || journal.Entries.Count > 5001 || journal.Status is not ("prepared" or "applying" or "committed" or "rolled-back" or "recovery-required"))
            throw new InvalidDataException("Invalid recovery journal.");
        return journal;
    }
    private string StatePath(HarnessAdapter adapter) => Path.Combine(_directory,
        adapter.Id + "-" + InstallerPaths.Hash(System.Text.Encoding.UTF8.GetBytes(adapter.Profile.ToUpperInvariant()))[..20] + ".json");
    private FileStream AcquireLock()
    {
        var path = Path.Combine(_directory, "installer.lock");
        InstallerPaths.CheckLocal(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("Otra instalación o recuperación está en curso. Reintenta cuando termine."); }
    }
    private void EnsurePrivateDirectory()
    {
        InstallerPaths.EnsurePrivateDirectory(_directory);
    }
    private static void Replace(string path, byte[] bytes, string? expected)
    {
        InstallerPaths.CheckLocal(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".estao-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDurable(temporary, bytes);
            if (InstallerPaths.FileHash(path) != expected) throw new IOException("Concurrent destination modification.");
            InstallerPaths.CheckLocal(path);
            File.Move(temporary, path, overwrite: expected is not null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void WriteDocument<T>(string path, T document) => Replace(path,
        JsonSerializer.SerializeToUtf8Bytes(document, InstallerJson.Options), InstallerPaths.FileHash(path));
    private static void WriteDurable(string path, byte[] bytes)
    {
        InstallerPaths.CheckLocal(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
    private static InstallResult Result(InstallPlan plan) => new(
        plan.Files.Count(f => f.Action == InstallAction.Create),
        plan.Files.Count(f => f.Action is InstallAction.Update or InstallAction.Merge),
        plan.Files.Count(f => f.Action == InstallAction.AlreadyPresent),
        plan.Files.Count(f => f.Action == InstallAction.Conflict), plan.Commit);
}
