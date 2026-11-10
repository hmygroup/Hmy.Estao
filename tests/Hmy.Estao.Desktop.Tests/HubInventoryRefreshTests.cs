using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Desktop.HarnessHub;

namespace Hmy.Estao.Desktop.Tests;

public sealed class HubInventoryRefreshTests
{
    [Fact]
    public async Task RootScanFailureIsReportedOnTheToolRatherThanAsABogusSkill()
    {
        var home = Path.Combine(Path.GetTempPath(), "estao-hub-root-error-" + Guid.NewGuid().ToString("N"));
        try
        {
            var adapter = new HarnessAdapter("codex", home, _ => null);
            Directory.CreateDirectory(Path.GetDirectoryName(adapter.SkillsRoot)!);
            File.WriteAllText(adapter.SkillsRoot, "This root is a file, not a directory.");
            using var session = new HarnessHubSession(Path.Combine(home, "data", "config.json"),
                detected: id => id == "codex", adapter: id => new HarnessAdapter(id, home, _ => null),
                candidates: _ => Task.FromResult<IReadOnlyList<string>>([]));
            session.Command(new("check"));
            await session.CurrentOperation;
            Assert.Null(session.State.Error);
            Assert.Empty(session.State.Skills!);
            var tool = Assert.Single(session.State.Tools, tool => tool.Id == "codex");
            Assert.Equal("Error", tool.Status);
            Assert.NotNull(tool.Error);
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
    }

    [Fact]
    public async Task RecheckingReplacesLocalOccurrencesAndRemovesDeletedSkillsWithoutGitHub()
    {
        var home = Path.Combine(Path.GetTempPath(), "estao-hub-refresh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var adapter = new HarnessAdapter("codex", home, _ => null);
            var directory = Path.Combine(adapter.SkillsRoot, "local");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "SKILL.md");
            File.WriteAllText(file, "---\nname: local\ndescription: A local skill\n---\n");
            using var session = new HarnessHubSession(Path.Combine(home, "data", "config.json"),
                detected: id => id == "codex", adapter: id => new HarnessAdapter(id, home, _ => null),
                candidates: _ => Task.FromResult<IReadOnlyList<string>>([]));

            session.Command(new("check"));
            await session.CurrentOperation;
            Assert.Null(session.State.Error);
            Assert.False(session.State.Connected);
            Assert.Single(Assert.Single(session.State.Skills!).Occurrences);

            session.Command(new("check"));
            await session.CurrentOperation;
            Assert.Null(session.State.Error);
            Assert.Single(Assert.Single(session.State.Skills!).Occurrences);

            File.Delete(file);
            session.Command(new("check"));
            await session.CurrentOperation;
            Assert.Null(session.State.Error);
            Assert.Empty(session.State.Skills!);
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
    }
}
