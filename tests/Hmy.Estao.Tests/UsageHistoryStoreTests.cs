using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Tests;

public sealed class UsageHistoryStoreTests
{
    [Fact]
    public async Task AppendPersistsUsageSamplesLocally()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"estao-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new UsageHistoryStore(Path.Combine(directory, "config.json"));
            var timestamp = DateTimeOffset.UtcNow;
            var snapshot = new UsageSnapshot(
                "codex", "Codex", "test", timestamp,
                [new RateWindow("session", "Session", .25D, timestamp.AddHours(2))]);

            var written = await store.AppendAsync([snapshot], timestamp);
            var loaded = await store.LoadAsync();

            var point = Assert.Single(loaded);
            Assert.Equal("codex", point.Provider);
            Assert.Equal("session", point.Window);
            Assert.Equal(.25D, point.PercentUsed);
            Assert.Equal(written.Count, loaded.Count);
            Assert.True(File.Exists(store.Path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
