using System.Runtime.InteropServices;
using System.Text.Json;
using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Desktop.HarnessHub;

internal sealed class HubNetworkInstaller(string dataDirectory, Func<string, HarnessAdapter> adapterFactory,
    Func<string, string?>? findExecutable = null, Func<string?>? readUserPath = null, Action<string>? writeUserPath = null)
{
    private readonly Func<string, string?> _find = findExecutable ?? CliExecutable.Find;
    private readonly Func<string?> _readPath = readUserPath ?? (() => Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
    private readonly Action<string> _writePath = writeUserPath ?? (value =>
    {
        Environment.SetEnvironmentVariable("PATH", value, EnvironmentVariableTarget.User);
        // Notify shells and subsequently launched applications, without changing machine PATH.
        if (OperatingSystem.IsWindows()) SendMessageTimeout(new IntPtr(0xffff), 0x1a, IntPtr.Zero, "Environment", 2, 1000, out _);
    });
    private string Root => Path.Combine(dataDirectory, "hub-packages");

    public static string CommandName(HubSkill item) => item.CommandName ??
        (item.Id.EndsWith("-cli", StringComparison.Ordinal) ? item.Id[..^4] : item.Id);

    public string? FindCli(HubSkill item)
    {
        var name = CommandName(item);
        // The running tray's process PATH may predate a user-level install.
        foreach (var raw in (_readPath() ?? "").Split(';'))
        {
            var directory = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
            {
                var path = Path.Combine(directory, name + extension);
                if (File.Exists(path)) return path;
            }
        }
        return _find(name) ?? (name != item.Id ? _find(item.Id) : null);
    }

    public async Task InstallAsync(HubSkill item, IReadOnlyList<string> targets, CancellationToken ct)
    {
        using var gate = Lock();
        ValidateItem(item);
        var source = item.Source!;
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("The catalog package folder is unavailable: " + source);
        var executables = Directory.GetFiles(source, "*.exe", SearchOption.TopDirectoryOnly);
        if (executables.Length != 1) throw new InvalidDataException("A catalog package must contain exactly one top-level executable.");
        var command = Path.GetFileNameWithoutExtension(executables[0]);
        ValidateName(command);
        if (item.ItemType == "cli" && !command.Equals(CommandName(item), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"CLI executable must be named {CommandName(item)}.exe (catalog ID, optionally without -cli).");
        var configs = new List<HubMcpConfiguration>();
        if (item.ItemType == "mcp")
        {
            foreach (var id in targets)
            {
                var adapter = adapterFactory(id);
                if (await new HarnessInstallationService(dataDirectory).GetInstallationStatusAsync(adapter) == HubOperationState.RecoveryRequired)
                    throw new InvalidOperationException("Recover the interrupted tool installation first.");
                configs.Add(await HubMcpConfiguration.ReadAsync(adapter, ct));
            }
            if (configs.Count == 0) throw new InvalidOperationException("No tools are selected for MCP installation.");
        }
        var destination = InstallerPaths.Within(Root, item.ItemType + "/" + item.Id + "/" + Guid.NewGuid().ToString("N"));
        InstallerPaths.EnsurePrivateDirectory(destination);
        var committed = false;
        var written = new List<HubMcpConfiguration>();
        var previousPath = _readPath() ?? "";
        var previousProcessPath = Environment.GetEnvironmentVariable("PATH");
        using var removal = new PackageRemoval(dataDirectory);
        try
        {
            await CopyAsync(source, destination, ct);
            var executable = InstallerPaths.Within(destination, Path.GetFileName(executables[0]));
            CheckRuntime(executable);
            ct.ThrowIfCancellationRequested();
            if (item.ItemType == "cli")
            {
                var old = FindCli(item with { CommandName = command });
                if (old is not null && !SamePath(old, executable)) removal.Stage([old]);
                // Replace the PATH location, not the executable inside an arbitrary shared bin folder.
                var oldDirectory = old is null ? null : Path.GetDirectoryName(old)!;
                var removeOldPath = oldDirectory is not null && !LocalFiles(oldDirectory).Any();
                var path = AddPath(removeOldPath ? RemovePath(previousPath, oldDirectory!) : previousPath, destination);
                SetPath(path, destination, true);
                if (removeOldPath && readUserPath is null)
                    Environment.SetEnvironmentVariable("PATH", RemovePath(Environment.GetEnvironmentVariable("PATH") ?? "", oldDirectory!));
            }
            else
            {
                var oldExecutables = configs.Select(config => config.PayloadExecutable(item.Id, strict: false))
                    .OfType<string>().Select(ResolveExecutable).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var config in configs) config.Install(item.Id, executable);
                if (oldExecutables.Length > 0)
                {
                    var remaining = configs.Concat(await Task.WhenAll(HarnessAdapter.Ids.Except(targets)
                        .Select(id => HubMcpConfiguration.ReadAsync(adapterFactory(id), ct)))).SelectMany(config => config.Executables())
                        .Select(ResolveExecutable).OfType<string>().ToArray();
                    removal.Stage(oldExecutables.Where(old => !remaining.Any(path => SamePath(path, old))));
                }
                foreach (var config in configs)
                {
                    await config.WriteAsync(dataDirectory, ct);
                    written.Add(config);
                }
            }
            removal.Complete();
            committed = true;
        }
        catch
        {
            if (item.ItemType == "cli")
            {
                _writePath(previousPath);
                if (readUserPath is null) Environment.SetEnvironmentVariable("PATH", previousProcessPath);
            }
            // Roll back only our writes; preserve concurrent edits rather than overwrite them.
            foreach (var config in written.AsEnumerable().Reverse())
            {
                await RestoreAsync(config, config.Path);
            }
            written.Clear();
            throw;
        }
        finally
        {
            // Retain staged payloads if rollback could not finish: configuration may reference them.
            if (!committed && written.Count == 0 && Directory.Exists(destination)) DeleteTree(destination);
        }
    }

    public async Task UninstallAsync(HubSkill item, IReadOnlyList<string> targets, CancellationToken ct)
    {
        using var gate = Lock();
        ValidateItem(item);
        if (item.ItemType == "cli")
        {
            var executable = FindCli(item);
            if (executable is null) return;
            ct.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(executable)!;
            var previousPath = _readPath() ?? "";
            var previousProcessPath = Environment.GetEnvironmentVariable("PATH");
            using var removal = new PackageRemoval(dataDirectory);
            removal.Stage([executable]);
            try
            {
                // A shared bin must stay on PATH for its other commands.
                if (!Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any())
                    SetPath(RemovePath(previousPath, directory), directory, false);
                removal.Complete();
            }
            catch
            {
                _writePath(previousPath);
                if (readUserPath is null) Environment.SetEnvironmentVariable("PATH", previousProcessPath);
                throw;
            }
            return;
        }
        var configs = new List<HubMcpConfiguration>();
        var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in targets)
        {
            var adapter = adapterFactory(id);
            if (await new HarnessInstallationService(dataDirectory).GetInstallationStatusAsync(adapter) == HubOperationState.RecoveryRequired)
                throw new InvalidOperationException("Recover the interrupted tool installation first.");
            var config = await HubMcpConfiguration.ReadAsync(adapter, ct);
            var executable = config.PayloadExecutable(item.Id);
            if (executable is not null)
            {
                executable = ResolveExecutable(executable);
                if (executable is not null) executables.Add(executable);
            }
            config.Remove(item.Id);
            configs.Add(config);
        }
        // Resolve references before changing anything, including other names in selected tools.
        if (executables.Count > 0)
        {
            var remaining = configs.Concat(await Task.WhenAll(HarnessAdapter.Ids.Except(targets)
                .Select(id => HubMcpConfiguration.ReadAsync(adapterFactory(id), ct)))).SelectMany(config => config.Executables())
                .Select(ResolveExecutable).OfType<string>().ToArray();
            executables.RemoveWhere(executable => remaining.Any(path => SamePath(path, executable)));
        }
        using var packageRemoval = new PackageRemoval(dataDirectory);
        packageRemoval.Stage(executables);
        var written = new List<HubMcpConfiguration>();
        try
        {
            foreach (var config in configs)
            {
                await config.WriteAsync(dataDirectory, ct);
                written.Add(config);
            }
            packageRemoval.Complete();
        }
        catch
        {
            foreach (var config in written.AsEnumerable().Reverse()) await RestoreAsync(config, config.Path);
            throw;
        }
    }

    private static async Task RestoreAsync(HubMcpConfiguration config, string path)
    {
        if (InstallerPaths.FileHash(path) != InstallerPaths.Hash(config.Serialize()))
            throw new IOException("MCP configuration changed during rollback. The package and configuration backup were retained.");
        if (config.BeforeHash is null) File.Delete(path);
        else await HubFileExporter.WriteAsync(path, config.Before, InstallerPaths.FileHash(path), CancellationToken.None);
    }

    private FileStream Lock()
    {
        var directory = Path.Combine(dataDirectory, "harness-installer");
        InstallerPaths.EnsurePrivateDirectory(directory);
        return new(InstallerPaths.Within(directory, "installer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string? ResolveExecutable(string command) => Path.IsPathFullyQualified(command) ? command : _find(command);

    private static void ValidateItem(HubSkill item)
    {
        ValidateName(item.Id);
        if (!item.Curated || !item.InLibrary || item.ItemType is not ("mcp" or "cli") || string.IsNullOrWhiteSpace(item.Source))
            throw new InvalidDataException("Select a catalog MCP or CLI package.");
    }

    private static void ValidateName(string name)
    {
        InstallerPaths.Relative(name);
        if (name.Contains('/') || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidDataException("Invalid package or command name.");
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        long total = 0;
        var count = 0;
        async Task CopyDirectory(string from, string to, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 30 || (File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Package contains a link or exceeds the directory depth limit.");
            foreach (var path in Directory.EnumerateFileSystemEntries(from))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > 20000 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Package contains a link or too many files.");
                var target = InstallerPaths.Within(to, Path.GetFileName(path));
                if (Directory.Exists(path))
                {
                    Directory.CreateDirectory(target);
                    await CopyDirectory(path, target, depth + 1);
                }
                else
                {
                    total += new FileInfo(path).Length;
                    if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Package exceeds the 2 GB limit.");
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, ct);
                }
            }
        }
        await CopyDirectory(source, destination, 0);
    }

    private static void CheckRuntime(string executable)
    {
        var path = Path.ChangeExtension(executable, ".runtimeconfig.json");
        if (!File.Exists(path)) return;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!document.RootElement.GetProperty("runtimeOptions").TryGetProperty("framework", out var framework)) return;
        var name = framework.GetProperty("name").GetString()!;
        var version = Version.Parse(framework.GetProperty("version").GetString()!);
        ValidateName(name);
        var roots = new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet") };
        if (!roots.Where(root => root is not null).Any(root =>
            Directory.Exists(Path.Combine(root!, "shared", name)) && Directory.EnumerateDirectories(Path.Combine(root!, "shared", name))
                .Any(directory => Version.TryParse(Path.GetFileName(directory), out var installed) &&
                    installed.Major == version.Major && installed.Minor == version.Minor && installed >= version)))
            throw new InvalidOperationException($"This package requires {name} {version.Major}.{version.Minor}. Install the matching .NET runtime before retrying.");
    }

    private static void ValidateRemoval(string executable)
    {
        InstallerPaths.CheckLocal(executable);
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(executable))!).DriveType == DriveType.Network)
            throw new InvalidDataException("Network package sources cannot be removed. Only local application copies can be uninstalled.");
        if (!Path.IsPathFullyQualified(executable) || !new[] { ".exe", ".cmd", ".bat", ".dll", ".js", ".py" }.Contains(Path.GetExtension(executable), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Cannot determine the installed package executable safely.");
        if (new[] { "dotnet", "node", "python", "python3", "cmd", "powershell", "pwsh", "npx", "npm", "uv", "uvx", "bash", "sh" }
            .Contains(Path.GetFileNameWithoutExtension(executable), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The MCP uses a shared runtime or package manager. Its application path must be explicit before its files can be removed.");
        var directory = Path.GetDirectoryName(executable)!;
        var protectedRoots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.GetPathRoot(directory)! };
        if (protectedRoots.Where(root => root.Length > 0).Any(root => SamePath(root, directory)) ||
            directory.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Refusing to remove an executable from a system or user-profile root.");
    }

    private static string[] RemovalFiles(string executable)
    {
        ValidateRemoval(executable);
        if (!File.Exists(executable)) return [];
        var directory = Path.GetDirectoryName(executable)!;
        var stem = Path.GetFileNameWithoutExtension(executable);
        // Published .NET applications include their dependencies and satellite resources.
        // Shared command directories only lose this command and its same-stem sidecars.
        var dedicated = (File.Exists(Path.ChangeExtension(executable, ".deps.json")) ||
                Path.GetFileName(directory).Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(directory).Equals(stem + "-cli", StringComparison.OrdinalIgnoreCase)) &&
            Directory.GetFiles(directory, "*.exe").All(path => SamePath(path, executable) ||
                Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase));
        var files = dedicated ? LocalFiles(directory).ToArray()
            : Directory.GetFiles(directory, stem + ".*").Where(path =>
                new[] { ".exe", ".cmd", ".bat", ".dll", ".pdb", ".json", ".config" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
        return files;
    }

    private static IEnumerable<string> LocalFiles(string directory)
    {
        InstallerPaths.CheckLocal(directory);
        foreach (var file in Directory.EnumerateFiles(directory)) { InstallerPaths.CheckLocal(file); yield return file; }
        foreach (var child in Directory.EnumerateDirectories(directory))
            foreach (var file in LocalFiles(child)) yield return file;
    }

    // Removal treats manual and Estao installations identically. Move files to
    // a private backup first, so configuration/PATH failures can restore them.
    private sealed class PackageRemoval(string dataDirectory) : IDisposable
    {
        private readonly List<(string Original, string Backup)> _files = [];
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        private bool _complete;

        public void Stage(IEnumerable<string> executables)
        {
            var paths = executables.SelectMany(executable =>
            {
                var root = Path.GetDirectoryName(executable)!;
                var files = RemovalFiles(executable);
                foreach (var file in files)
                    for (var parent = Path.GetDirectoryName(file); parent is not null; parent = Path.GetDirectoryName(parent))
                    {
                        _directories.Add(parent);
                        if (SamePath(parent, root)) break;
                    }
                return files;
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Preflight every handle before moving any file (including running executables).
            var handles = new List<FileStream>();
            var backup = Path.Combine(dataDirectory, "network-backups", Guid.NewGuid().ToString("N"), "files");
            try
            {
                foreach (var path in paths)
                {
                    InstallerPaths.CheckLocal(path);
                    handles.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete));
                }
                if (paths.Length > 0) InstallerPaths.EnsurePrivateDirectory(backup);
                // Keep a human-readable recovery map, also useful after an app interruption.
                if (paths.Length > 0) File.WriteAllText(Path.Combine(backup, "recovery.json"), JsonSerializer.Serialize(paths));
                for (var i = 0; i < paths.Length; i++)
                {
                    var target = Path.Combine(backup, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    File.Move(paths[i], target);
                    _files.Add((paths[i], target));
                }
            }
            finally { foreach (var handle in handles) handle.Dispose(); }
        }

        public void Complete()
        {
            // Backups deliberately remain available alongside configuration backups.
            foreach (var directory in _directories.OrderByDescending(path => path.Length))
            {
                InstallerPaths.CheckLocal(directory);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            _complete = true;
        }

        public void Dispose()
        {
            if (_complete) return;
            foreach (var (original, backup) in _files.AsEnumerable().Reverse())
            {
                InstallerPaths.CheckLocal(original);
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Move(backup, original, overwrite: false);
            }
        }
    }

    private static void DeleteTree(string directory)
    {
        InstallerPaths.CheckLocal(directory);
        foreach (var file in LocalFiles(directory))
        { InstallerPaths.CheckLocal(file); File.Delete(file); }
        RemoveEmptyDirectories(directory);
    }

    private static void RemoveEmptyDirectories(string directory)
    {
        InstallerPaths.CheckLocal(directory);
        foreach (var child in Directory.GetDirectories(directory)) RemoveEmptyDirectories(child);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    internal static string AddPath(string path, string directory) => string.Join(';', new[] { directory }
        .Concat(path.Split(';').Where(part => !SamePath(Environment.ExpandEnvironmentVariables(part.Trim().Trim('"')), directory) && part.Length > 0)));
    internal static string RemovePath(string path, string directory) => string.Join(';', path.Split(';')
        .Where(part => !SamePath(Environment.ExpandEnvironmentVariables(part.Trim().Trim('"')), directory)));

    private void SetPath(string value, string directory, bool add)
    {
        _writePath(value);
        if (readUserPath is null)
        {
            var process = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", add ? AddPath(process, directory) : RemovePath(process, directory));
        }
    }

    private static bool SamePath(string left, string right) => left.TrimEnd('\\', '/').Equals(right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);
}
