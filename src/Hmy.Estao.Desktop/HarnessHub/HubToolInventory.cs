using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Desktop.HarnessHub;

internal static class HubToolInventory
{
    public static async Task<IReadOnlyList<HubSkill>> ScanAsync(IEnumerable<string> toolIds,
        Func<string, HarnessAdapter> adapterFactory, IEnumerable<HubSkill> catalog,
        CancellationToken cancellationToken = default, Func<string, string?>? findExecutable = null)
        => Merge(await ReadAsync(toolIds, adapterFactory, cancellationToken), catalog, cancellationToken, findExecutable);

    internal sealed record Snapshot(Inventory Mcp, Inventory Instructions);

    internal sealed class Inventory(StringComparer comparer)
    {
        public Dictionary<string, List<HubSkillOccurrence>> Occurrences { get; } = new(comparer);
        public Dictionary<string, string> Failures { get; } = new(StringComparer.Ordinal);

        public void Add(string itemId, HubSkillOccurrence occurrence)
        {
            if (!Occurrences.TryGetValue(itemId, out var copies)) Occurrences[itemId] = copies = [];
            copies.Add(occurrence);
        }
    }

    internal static async Task<Snapshot> ReadAsync(IEnumerable<string> toolIds,
        Func<string, HarnessAdapter> adapterFactory, CancellationToken cancellationToken)
    {
        var ids = toolIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var mcp = new Inventory(StringComparer.OrdinalIgnoreCase);
        var instructions = new Inventory(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HarnessAdapter adapter;
            try { adapter = adapterFactory(id); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                mcp.Failures[id] = "Could not read MCP configuration.";
                instructions.Failures[id] = "Could not read personal instructions.";
                continue;
            }
            try
            {
                foreach (var name in await adapter.ReadMcpServerNamesAsync(cancellationToken))
                    mcp.Add(name, new(id, adapter.McpPath, false, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Do not expose configuration contents or secrets in discovery diagnostics.
                mcp.Failures[id] = "Could not read MCP configuration.";
            }
            try
            {
                var path = HubInstructionInstaller.Destination(adapter);
                foreach (var instructionId in await HubInstructionInstaller.ReadInstalledIdsAsync(path, cancellationToken))
                    instructions.Add(instructionId, new(id, path, false, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                instructions.Failures[id] = "Could not read personal instructions.";
            }
        }

        return new(mcp, instructions);
    }

    internal static IReadOnlyList<HubSkill> Merge(Snapshot snapshot, IEnumerable<HubSkill> catalog,
        CancellationToken cancellationToken, Func<string, string?>? findExecutable = null)
    {
        var occurrences = new Dictionary<string, List<HubSkillOccurrence>>(snapshot.Mcp.Occurrences, StringComparer.OrdinalIgnoreCase);
        var failures = snapshot.Mcp.Failures;
        var instructionErrors = snapshot.Instructions.Failures.Select(pair => new HubSkillOccurrence(pair.Key, "", false, null, pair.Value)).ToArray();
        var result = new List<HubSkill>();
        foreach (var item in catalog.Where(item => item.ItemType is "mcp" or "cli" or "instruction"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ItemType == "instruction")
            {
                var installed = snapshot.Instructions.Occurrences.GetValueOrDefault(item.Id) ?? [];
                result.Add(item with { Occurrences = installed.Concat(instructionErrors).ToArray() });
                continue;
            }
            IReadOnlyList<HubSkillOccurrence> copies;
            if (item.ItemType == "mcp")
            {
                copies = occurrences.GetValueOrDefault(item.Id) ?? [];
                copies = copies.Concat(failures.Select(pair => new HubSkillOccurrence(pair.Key, "", false, null, pair.Value))).ToArray();
                occurrences.Remove(item.Id);
            }
            else
            {
                var name = HubPackageInstaller.CommandName(item);
                var executable = (findExecutable ?? CliExecutable.Find)(name) ?? (name != item.Id ? (findExecutable ?? CliExecutable.Find)(item.Id) : null);
                // CLIs are user-global even when no harness is detected.
                copies = executable is null ? [] : [new("global", executable, true, null)];
            }
            result.Add(item with { CompatibleTools = item.ItemType == "mcp" ? HarnessAdapter.Ids : [], Occurrences = copies,
                Installable = true, CommandName = item.ItemType == "cli" ? HubPackageInstaller.CommandName(item) : item.Id });
        }
        result.AddRange(occurrences.Select(pair => new HubSkill("mcp:" + pair.Key, pair.Key, "", false, false,
            HarnessAdapter.Ids, pair.Value.Concat(failures.Select(failure =>
                new HubSkillOccurrence(failure.Key, "", false, null, failure.Value))).ToArray(), ItemType: "mcp", CommandName: pair.Key)));
        return result;
    }
}
