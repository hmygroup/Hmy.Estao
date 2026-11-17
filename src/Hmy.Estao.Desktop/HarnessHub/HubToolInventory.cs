using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Desktop.HarnessHub;

internal static class HubToolInventory
{
    public static async Task<IReadOnlyList<HubSkill>> ScanAsync(IEnumerable<string> toolIds,
        Func<string, HarnessAdapter> adapterFactory, IEnumerable<HubSkill> catalog,
        CancellationToken cancellationToken = default, Func<string, string?>? findExecutable = null)
    {
        var ids = toolIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var occurrences = new Dictionary<string, List<HubSkillOccurrence>>(StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, string>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var adapter = adapterFactory(id);
                foreach (var name in await adapter.ReadMcpServerNamesAsync(cancellationToken))
                {
                    if (!occurrences.TryGetValue(name, out var copies)) occurrences[name] = copies = [];
                    copies.Add(new(id, adapter.McpPath, false, null));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Do not expose configuration contents or secrets in discovery diagnostics.
                failures[id] = "Could not read MCP configuration.";
            }
        }

        var result = new List<HubSkill>();
        foreach (var item in catalog.Where(item => item.ItemType is "mcp" or "cli"))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                Installable = true, CommandName = item.ItemType == "cli" ? HubPackageInstaller.CommandName(item) : null });
        }
        result.AddRange(occurrences.Select(pair => new HubSkill("mcp:" + pair.Key, pair.Key, "", false, false,
            HarnessAdapter.Ids, pair.Value.Concat(failures.Select(failure =>
                new HubSkillOccurrence(failure.Key, "", false, null, failure.Value))).ToArray(), ItemType: "mcp")));
        return result;
    }
}
