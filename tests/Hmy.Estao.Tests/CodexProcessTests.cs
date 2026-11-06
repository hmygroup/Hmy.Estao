using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Tests;

public sealed class CodexProcessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "estao-codex-launch-" + Guid.NewGuid().ToString("N"));

    public CodexProcessTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void FindsNativeExeWithoutRequiringAnNpmShim()
    {
        var executable = Path.Combine(_directory, "codex.exe");
        File.WriteAllText(executable, "fixture, not executable");
        Assert.Equal(executable, CodexProcess.FindIn([_directory], windows: true));
    }

    [Theory]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    public void FindsLegacyWrappers(string extension)
    {
        var executable = Path.Combine(_directory, "codex" + extension);
        File.WriteAllText(executable, "fixture");
        Assert.Equal(executable, CodexProcess.FindIn([$"\"{_directory}\""], windows: true));
        var info = CodexProcess.CreateStartInfo(executable, _directory, ["-s", "read-only", "-a", "untrusted", "app-server"]);
        Assert.Contains("/d /s /c", info.Arguments);
        Assert.Contains("app-server", info.Arguments);
        Assert.True(info.CreateNoWindow);
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void UnixUsesTheExtensionlessExecutable()
    {
        var executable = Path.Combine(_directory, "codex");
        File.WriteAllText(executable, "fixture");
        Assert.Equal(executable, CodexProcess.FindIn([_directory], windows: false));
    }

    [Fact]
    public void MissingExecutableDoesNotFallBackToANonexistentCmd()
    {
        Assert.Null(CodexProcess.FindIn([_directory], windows: true));
    }

    [Fact]
    public void NativeLaunchSetsProfileButDoesNotRequireItAsTheWorkingDirectory()
    {
        var profile = Path.Combine(_directory, "missing-profile");
        var info = CodexProcess.CreateStartInfo(Path.Combine(_directory, "codex.exe"), profile, ["app-server"]);
        Assert.Equal(profile, info.Environment["CODEX_HOME"]);
        Assert.NotEqual(profile, info.WorkingDirectory);
        Assert.True(Directory.Exists(info.WorkingDirectory));
        Assert.False(Directory.Exists(profile));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
