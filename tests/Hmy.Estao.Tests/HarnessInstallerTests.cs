using System.Net;
using System.Text;
using System.Text.Json;
using Hmy.Estao.Core.Harnesses.Installer;

namespace Hmy.Estao.Tests;

public sealed class HarnessInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "estao-installer-tests-" + Guid.NewGuid().ToString("N"));
    private HarnessAdapter Adapter(string id = "claude") => new(id, Path.Combine(_root, "home"), _ => null);
    private HarnessInstallationService Service(Action<string>? fault = null) => new(Path.Combine(_root, "data"), fault);
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static PreparedSnapshot Snapshot(params PreparedFile[] files) => new(new string('a', 40), new string('b', 40), "test-1", files, []);
    private static PreparedFile FileFor(HarnessAdapter adapter, string content, string id = "sample", string leaf = "SKILL.md") =>
        new(id, Path.Combine(adapter.SkillsRoot, id, leaf), Bytes(content));

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../b")]
    [InlineData("C:/secrets")]
    [InlineData("a\\b")]
    [InlineData("a:secret")]
    [InlineData("CON.txt")]
    [InlineData("lpt1")]
    [InlineData("x.")]
    [InlineData("x ")]
    [InlineData("a//b")]
    [InlineData("/absolute")]
    public void rejects_unsafe_source_paths(string value) => Assert.Throws<InvalidDataException>(() => InstallerPaths.Relative(value));

    [Theory]
    [InlineData("codex", ".agents")]
    [InlineData("claude", ".claude")]
    [InlineData("copilot", ".copilot")]
    [InlineData("opencode", ".config")]
    public void resolves_native_personal_skill_locations(string id, string folder)
    {
        var adapter = Adapter(id);
        Assert.Contains(folder, adapter.SkillsRoot);
        Assert.StartsWith(Path.Combine(_root, "home"), adapter.SkillsRoot);
        Assert.False(adapter.IsAllowedDestination(Path.Combine(_root, "outside.txt")));
    }

    [Theory]
    [InlineData("codex", "CODEX_HOME")]
    [InlineData("claude", "CLAUDE_CONFIG_DIR")]
    [InlineData("copilot", "COPILOT_HOME")]
    [InlineData("opencode", "OPENCODE_CONFIG_DIR")]
    public void respects_absolute_profile_overrides(string id, string key)
    {
        var profile = Path.Combine(_root, "custom");
        var adapter = new HarnessAdapter(id, Path.Combine(_root, "home"), name => name == key ? profile : null);
        Assert.Equal(profile, adapter.Profile);
        Assert.Throws<InvalidDataException>(() => new HarnessAdapter(id, _root, name => name == key ? "relative" : null));
    }

    [Fact]
    public void validates_versions_and_rejects_unproven_agent_conversions()
    {
        var artifact = new SourceArtifact { Id = "agent", Category = "agent", MinimumVersions = new() { ["claude"] = "2.1" } };
        Assert.Throws<InvalidDataException>(() => Adapter().CheckCapabilities(artifact));
        var known = new HarnessAdapter("claude", _root, _ => null, new Version(2, 2));
        known.CheckCapabilities(artifact);
        Assert.Throws<InvalidDataException>(() => Adapter("opencode").CheckCapabilities(new() { Category = "mcp" }));
        Assert.Throws<InvalidDataException>(() => Adapter("copilot").CheckCapabilities(new() { Category = "command" }));
    }

    [Fact]
    public void reads_optional_skill_translations_without_changing_the_standard_description()
    {
        var bytes = Bytes("---\nname: sample\ndescription: Standard fallback\ndescriptions:\n  en: English translation\n  es: Descripción española\n  fr: >-\n    Description\n    française\n---\nBody\n");
        var metadata = HarnessAdapter.ReadSkillMetadata(bytes);
        Assert.Equal("sample", metadata.Name);
        Assert.Equal("Standard fallback", metadata.Description);
        Assert.Equal("Descripción española", metadata.Descriptions["es"]);
        Assert.Equal("Description française", metadata.Descriptions["fr"]);
        Assert.Equal(("sample", "Standard fallback"), HarnessAdapter.SkillMetadata(bytes));
        Assert.Empty(HarnessAdapter.ReadSkillMetadata(Bytes("---\nname: legacy\ndescription: Legacy fallback\n---\n")).Descriptions);
    }

    [Theory]
    [InlineData("descriptions: invalid")]
    [InlineData("descriptions: [English, Spanish]")]
    [InlineData("descriptions:\n  EN: Invalid language")]
    [InlineData("descriptions:\n  zz: Unknown language")]
    [InlineData("descriptions:\n  es: ''")]
    [InlineData("descriptions:\n  es: [Nested, text]")]
    [InlineData("descriptions:\n  es: &text Spanish\n  fr: *text")]
    public void rejects_invalid_skill_translation_metadata(string translations)
    {
        Assert.Throws<InvalidDataException>(() => HarnessAdapter.ReadSkillMetadata(
            Bytes("---\nname: sample\ndescription: Fallback\n" + translations + "\n---\n")));
    }

    [Fact]
    public void preserves_native_skill_bytes_and_rejects_yaml_aliases()
    {
        var adapter = Adapter();
        var artifact = new SourceArtifact { Id = "sample", Source = "skills/sample", Harnesses = ["claude"] };
        var bytes = Bytes("---\nname: sample\ndescription: Explicit workflow\ndescriptions:\n  es: Flujo explícito\n---\n# Original body\nKeep all meaning.\n");
        var entry = new SourceEntry("skills/sample/SKILL.md", InstallerPaths.GitBlob(bytes), bytes.Length, "100644");
        Assert.Equal(bytes, Assert.Single(adapter.Prepare(artifact, [(entry, bytes)])).Content);
        var unsafeYaml = Bytes("---\nname: &loop [*loop]\ndescription: Unsafe\n---\n");
        Assert.Throws<InvalidDataException>(() => adapter.Prepare(artifact, [(entry, unsafeYaml)]));
        Assert.Throws<InvalidDataException>(() => adapter.CheckCapabilities(new SourceArtifact { Category = "mcp" }));
    }

    [Fact]
    public async Task creates_then_repeats_without_rewriting_or_duplicate_transactions()
    {
        var adapter = Adapter();
        var source = Snapshot(FileFor(adapter, "content"));
        var service = Service();
        var first = await service.ApplyAsync(adapter, await service.PlanAsync(adapter, source), false);
        Assert.Equal(1, first.Created);
        var target = source.Files[0].RelativePath;
        var timestamp = System.IO.File.GetLastWriteTimeUtc(target);
        var journalsBefore = Directory.GetDirectories(Path.Combine(_root, "data", "harness-installer", "transactions")).Length;
        var second = await service.ApplyAsync(adapter, await service.PlanAsync(adapter, source), false);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(timestamp, System.IO.File.GetLastWriteTimeUtc(target));
        Assert.Equal(journalsBefore, Directory.GetDirectories(Path.Combine(_root, "data", "harness-installer", "transactions")).Length);
        Assert.Equal(HubOperationState.Installed, await service.GetInstallationStatusAsync(adapter));
    }

    [Fact]
    public async Task identical_preexisting_file_is_not_claimed_or_later_overwritten()
    {
        var adapter = Adapter();
        var source = Snapshot(FileFor(adapter, "same"));
        Write(source.Files[0].RelativePath, "same");
        var service = Service();
        await service.ApplyAsync(adapter, await service.PlanAsync(adapter, source), false);
        var update = await service.PlanAsync(adapter, Snapshot(FileFor(adapter, "new")));
        Assert.Equal(InstallAction.Conflict, Assert.Single(update.Files).Action);
        Assert.Equal("same", await System.IO.File.ReadAllTextAsync(source.Files[0].RelativePath));
    }

    [Fact]
    public async Task updates_only_intact_managed_files_and_never_deletes_retired_content()
    {
        var adapter = Adapter();
        var service = Service();
        var first = Snapshot(FileFor(adapter, "v1"));
        await service.ApplyAsync(adapter, await service.PlanAsync(adapter, first), false);
        var update = await service.PlanAsync(adapter, Snapshot(FileFor(adapter, "v2")));
        Assert.Equal(InstallAction.Update, update.Files[0].Action);
        await service.ApplyAsync(adapter, update, false);
        Write(first.Files[0].RelativePath, "user-edit");
        Assert.Equal(InstallAction.Conflict, (await service.PlanAsync(adapter, first)).Files[0].Action);
        await service.ApplyAsync(adapter, await service.PlanAsync(adapter, Snapshot()), false);
        Assert.Equal("user-edit", await System.IO.File.ReadAllTextAsync(first.Files[0].RelativePath));
    }

    [Fact]
    public async Task a_resource_conflict_skips_its_entire_skill_and_dependent_group()
    {
        var adapter = Adapter();
        var first = FileFor(adapter, "skill");
        var resource = FileFor(adapter, "new", leaf: "reference.md");
        Write(resource.RelativePath, "mine");
        var snapshot = Snapshot(first, resource, FileFor(adapter, "dependent", "dependent")) with
        { Dependencies = new Dictionary<string, IReadOnlyList<string>> { ["dependent"] = new[] { "sample" } } };
        var plan = await Service().PlanAsync(adapter, snapshot);
        Assert.All(plan.Files, f => Assert.Equal(InstallAction.Conflict, f.Action));
        await Service().ApplyAsync(adapter, plan, true);
        Assert.False(System.IO.File.Exists(first.RelativePath));
        Assert.Equal("mine", await System.IO.File.ReadAllTextAsync(resource.RelativePath));
    }

    [Fact]
    public async Task detects_duplicate_destinations_and_concurrent_modification()
    {
        var adapter = Adapter();
        var service = Service();
        var file = FileFor(adapter, "one");
        var duplicate = await service.PlanAsync(adapter, Snapshot(file, file with { Content = Bytes("different") }));
        Assert.Equal(InstallAction.Conflict, Assert.Single(duplicate.Files).Action);
        var equal = await service.PlanAsync(adapter, Snapshot(file, file));
        Assert.Single(equal.Files);
        Write(file.RelativePath, "concurrent");
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(adapter, equal, false));
        Assert.Equal("concurrent", await System.IO.File.ReadAllTextAsync(file.RelativePath));
    }

    [Fact]
    public async Task compatible_discovery_roots_do_not_create_duplicate_skills_or_shadow_user_content()
    {
        var adapter = Adapter("copilot");
        var file = FileFor(adapter, "same");
        var shadow = Path.Combine(_root, "home", ".agents", "skills", "sample", "SKILL.md");
        Write(shadow, "same");
        var plan = await Service().PlanAsync(adapter, Snapshot(file));
        Assert.Equal(InstallAction.AlreadyPresent, Assert.Single(plan.Files).Action);
        await Service().ApplyAsync(adapter, plan, false);
        Assert.False(System.IO.File.Exists(file.RelativePath));
        Write(shadow, "my changes");
        var conflict = await Service().PlanAsync(adapter, Snapshot(file));
        Assert.Equal(InstallAction.Conflict, conflict.Files[0].Action);
        Assert.False(System.IO.File.Exists(file.RelativePath));
    }

    [Theory]
    [InlineData("before-state")]
    [InlineData("after-file")]
    public async Task rolls_back_failed_files_and_state_writes(string fault)
    {
        var adapter = Adapter();
        var original = Snapshot(FileFor(adapter, "v1"));
        var service = Service();
        await service.ApplyAsync(adapter, await service.PlanAsync(adapter, original), false);
        var failing = Service(point => { if (point == fault) throw new IOException("Injected failure"); });
        var source = Snapshot(FileFor(adapter, "v2"), FileFor(adapter, "new", "new"));
        await Assert.ThrowsAsync<IOException>(() => failing.ApplyAsync(adapter, failing.PlanAsync(adapter, source).Result, false));
        Assert.Equal("v1", await System.IO.File.ReadAllTextAsync(original.Files[0].RelativePath));
        Assert.False(System.IO.File.Exists(source.Files[1].RelativePath));
        Assert.Equal(HubOperationState.Installed, await service.GetInstallationStatusAsync(adapter));
    }

    [Fact]
    public async Task recovery_preserves_edits_made_after_transaction_write()
    {
        var adapter = Adapter();
        var file = FileFor(adapter, "installed");
        var failing = Service(point =>
        {
            if (point != "after-file") return;
            Write(file.RelativePath, "user-after-write");
            throw new IOException("Interrupted");
        });
        await Assert.ThrowsAsync<IOException>(() => failing.ApplyAsync(adapter, failing.PlanAsync(adapter, Snapshot(file)).Result, false));
        Assert.Equal(HubOperationState.RecoveryRequired, await Service().GetInstallationStatusAsync(adapter));
        Assert.NotEmpty(await Service().RecoverAsync(adapter));
        Assert.Equal("user-after-write", await System.IO.File.ReadAllTextAsync(file.RelativePath));
    }

    [Fact]
    public async Task recovers_persisted_interruption_on_a_new_service_instance()
    {
        var adapter = Adapter();
        var file = FileFor(adapter, "installed");
        var directory = Path.Combine(_root, "data", "harness-installer", "transactions", "interrupted");
        Directory.CreateDirectory(directory);
        Write(file.RelativePath, "installed");
        var journal = new TransactionJournal { HarnessId = adapter.Id, Profile = adapter.Profile, Status = "applying",
            Entries = [new(file.RelativePath, null, InstallerPaths.Hash(file.Content), "0.before")] };
        await System.IO.File.WriteAllBytesAsync(Path.Combine(directory, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(journal, InstallerJson.Options));
        Assert.Empty(await Service().RecoverAsync(adapter));
        Assert.False(System.IO.File.Exists(file.RelativePath));
    }

    [Fact]
    public async Task cancellation_prevents_installation()
    {
        var adapter = Adapter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var plan = await Service().PlanAsync(adapter, Snapshot(FileFor(adapter, "data")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ApplyAsync(adapter, plan, false, ct: cancellation.Token));
        Assert.False(System.IO.File.Exists(plan.Files[0].Path));
    }

    [Fact]
    public async Task concurrent_installation_lock_and_outside_targets_are_rejected()
    {
        var adapter = Adapter();
        var service = Service();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PlanAsync(adapter,
            Snapshot(new PreparedFile("escape", Path.Combine(_root, "outside.txt"), Bytes("no")))));
        var plan = await service.PlanAsync(adapter, Snapshot(FileFor(adapter, "new")));
        var directory = Path.Combine(_root, "data", "harness-installer");
        Directory.CreateDirectory(directory);
        using var locked = new FileStream(Path.Combine(directory, "installer.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(adapter, plan, false));
        Assert.False(System.IO.File.Exists(plan.Files[0].Path));
    }

    [Fact]
    public void inline_opencode_configuration_does_not_block_independent_skill_installation()
    {
        var adapter = new HarnessAdapter("opencode", Path.Combine(_root, "profile"), key => key == "OPENCODE_CONFIG_CONTENT" ? "{}" : null);
        adapter.CheckCapabilities(new SourceArtifact { Category = "skill" });
        Assert.Throws<InvalidDataException>(() => adapter.CheckCapabilities(new SourceArtifact { Category = "mcp" }));
    }

    [Fact]
    public void absolute_profile_paths_cannot_hide_alternate_streams_or_reserved_names()
    {
        Assert.Throws<InvalidDataException>(() => InstallerPaths.CheckLocal(Path.Combine(_root, "data:stream")));
        Assert.Throws<InvalidDataException>(() => new HarnessAdapter("claude", _root, key => key == "CLAUDE_CONFIG_DIR" ? Path.Combine(_root, "CON") : null));
    }

    [Theory]
    [InlineData("{ // user comment\n\"model\":\"mine\", \"mcpServers\": {\"mine\": {\"command\":\"keep\"}}, }")]
    [InlineData("{\"mcpServers\": {/* comment */}, \"model\":\"mine\"}")]
    [InlineData("{/* comment */\"model\":\"mine\"}")]
    public void merges_jsonc_without_replacing_comments_or_unrelated_configuration(string original)
    {
        var incoming = Bytes("{\"mcpServers\":{\"team\":{\"type\":\"stdio\",\"command\":\"new\"}}}");
        var merged = StructuredConfiguration.Merge(Bytes(original), incoming, "jsonc");
        var text = Encoding.UTF8.GetString(merged);
        Assert.Contains("mine", text);
        Assert.Contains("comment", text);
        Assert.Equal(merged, StructuredConfiguration.Merge(merged, incoming, "jsonc"));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("[]")]
    public void malformed_and_duplicate_json_is_never_reset(string original) =>
        Assert.ThrowsAny<Exception>(() => StructuredConfiguration.Merge(Bytes(original), Bytes("{\"a\":3}"), "jsonc"));

    [Fact]
    public void configuration_value_conflicts_are_not_overwritten()
    {
        Assert.Throws<InvalidDataException>(() => StructuredConfiguration.Merge(Bytes("{\"mcpServers\":{\"x\":{\"command\":\"mine\"}}}"),
            Bytes("{\"mcpServers\":{\"x\":{\"command\":\"team\"}}}"), "jsonc"));
        var before = "# keep me\nmodel = \"mine\"\n";
        var merged = Encoding.UTF8.GetString(StructuredConfiguration.Merge(Bytes(before), Bytes("[mcp_servers.team]\ncommand = \"tool\"\n"), "toml"));
        Assert.StartsWith(before, merged);
        Assert.Throws<InvalidDataException>(() => StructuredConfiguration.Merge(Bytes(merged), Bytes("[mcp_servers.other]\ncommand = \"other\""), "toml"));
    }

    [Fact]
    public async Task structured_merge_is_planned_and_records_nodes_without_claiming_unrelated_values()
    {
        var adapter = Adapter();
        Write(adapter.McpPath, "{// user comment\n\"identity\":\"private-local\"}");
        var file = new PreparedFile("mcp", adapter.McpPath, Bytes("{\"mcpServers\":{\"team\":{\"command\":\"tool\"}}}"), "jsonc");
        var service = Service();
        var plan = await service.PlanAsync(adapter, Snapshot(file));
        Assert.Equal(InstallAction.Merge, plan.Files[0].Action);
        Assert.DoesNotContain("/identity", plan.Files[0].StructuredNodes!.Keys);
        await service.ApplyAsync(adapter, plan, false);
        Assert.Contains("private-local", await System.IO.File.ReadAllTextAsync(adapter.McpPath));
        Assert.Equal(InstallAction.AlreadyPresent, (await service.PlanAsync(adapter, Snapshot(file))).Files[0].Action);
    }

    [Fact]
    public async Task file_lock_and_reparse_points_do_not_enable_unsafe_writes()
    {
        var adapter = Adapter();
        var file = FileFor(adapter, "data");
        Write(file.RelativePath, "mine");
        using (var locked = new FileStream(file.RelativePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(InstallAction.Conflict, (await Service().PlanAsync(adapter, Snapshot(file))).Files[0].Action);
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(adapter.SkillsRoot, "linked");
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            Assert.NotNull(process);
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, outside);
        Assert.Throws<InvalidDataException>(() => InstallerPaths.CheckLocal(Path.Combine(link, "file")));
        Directory.Delete(link);
    }

    [Fact]
    public async Task legacy_state_and_backups_are_never_migrated_or_deleted()
    {
        var legacy = Path.Combine(_root, "home", ".estao", "environment.json");
        Write(legacy, "legacy bytes");
        var adapter = Adapter();
        var service = Service();
        await service.ApplyAsync(adapter, await service.PlanAsync(adapter, Snapshot(FileFor(adapter, "new"))), false);
        Assert.Equal("legacy bytes", await System.IO.File.ReadAllTextAsync(legacy));
    }

    [Fact]
    public void ui_operation_disallows_duplicate_start_and_has_explicit_terminal_states()
    {
        var operation = new HubOperation();
        Assert.Equal(HubOperationState.NotConnected, operation.State);
        operation.Begin();
        Assert.Throws<InvalidOperationException>(operation.Begin);
        operation.Set(HubOperationState.Planning);
        Assert.True(operation.IsBusy);
        foreach (var state in new[] { HubOperationState.Installed, HubOperationState.PartiallyInstalled, HubOperationState.Error, HubOperationState.RecoveryRequired, HubOperationState.Conflict })
        {
            operation.Set(state);
            Assert.False(operation.IsBusy);
        }
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
