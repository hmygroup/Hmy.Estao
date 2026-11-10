using System.Text;
using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Desktop.HarnessHub;

namespace Hmy.Estao.Desktop.Tests;

public sealed class HubFileExporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "estao-hub-export-" + Guid.NewGuid().ToString("N"));

    public HubFileExporterTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ExportCreatesOrAtomicallyReplacesOnlyTheReviewedDestination()
    {
        var path = Path.Combine(_directory, "guidance.md");
        await HubFileExporter.WriteAsync(path, Encoding.UTF8.GetBytes("first"), null, CancellationToken.None);
        var before = InstallerPaths.FileHash(path);
        await HubFileExporter.WriteAsync(path, Encoding.UTF8.GetBytes("second"), before, CancellationToken.None);
        Assert.Equal("second", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ExportPreservesConcurrentChangesAndCleansUpTemporaryFiles()
    {
        var path = Path.Combine(_directory, "guidance.md");
        await File.WriteAllTextAsync(path, "old");
        var before = InstallerPaths.FileHash(path);
        await File.WriteAllTextAsync(path, "personal change");
        await Assert.ThrowsAsync<IOException>(() => HubFileExporter.WriteAsync(path, Encoding.UTF8.GetBytes("download"), before, CancellationToken.None));
        Assert.Equal("personal change", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancellationDoesNotTruncateAnExistingDestination()
    {
        var path = Path.Combine(_directory, "guidance.md");
        await File.WriteAllTextAsync(path, "personal");
        var before = InstallerPaths.FileHash(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HubFileExporter.WriteAsync(path, Encoding.UTF8.GetBytes("download"), before, cancellation.Token));
        Assert.Equal("personal", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
