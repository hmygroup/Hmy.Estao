using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Desktop.HarnessHub;

// Exports are explicit Save As operations, never automatic edits to a tool's configuration.
internal static class HubFileExporter
{
    public static async Task WriteAsync(string destination, byte[] content, string? expectedHash, CancellationToken ct)
    {
        destination = Path.GetFullPath(destination);
        InstallerPaths.CheckLocal(destination);
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, ".estao-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
            {
                created = true;
                await stream.WriteAsync(content, ct);
            }
            ct.ThrowIfCancellationRequested();
            if (InstallerPaths.FileHash(destination) != expectedHash)
                throw new IOException("The export destination changed after selection. Export again to review the destination.")
                    .WithUserMessage("hub.review_changed");
            InstallerPaths.CheckLocal(destination);
            // Optimistic concurrency, not a filesystem compare-and-swap: as with skill installation,
            // a same-user writer racing between OS calls is outside this check's isolation guarantees.
            File.Move(temporary, destination, overwrite: expectedHash is not null);
        }
        finally
        {
            if (created && File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
