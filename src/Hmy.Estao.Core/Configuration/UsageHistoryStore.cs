using System.Text.Json;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Core.Configuration;

/// <summary>
/// Stores usage samples locally beside Estao's configuration. The provider APIs
/// are only used to create a new sample; the chart reads the persisted samples.
/// </summary>
public sealed class UsageHistoryStore
{
    private const int Version = 1;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private const int MaxPointsPerSeries = 500;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UsageHistoryStore(string configPath)
    {
        var directory = System.IO.Path.GetDirectoryName(configPath);
        _path = System.IO.Path.Combine(string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory,
            "usage-history.json");
    }

    public string Path => _path;

    public async Task<IReadOnlyList<UsageHistoryPoint>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<UsageHistoryPoint>> AppendAsync(
        IReadOnlyList<UsageSnapshot> snapshots,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var points = (await ReadCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var sampleTime = timestamp ?? DateTimeOffset.UtcNow;

            foreach (var snapshot in snapshots)
            {
                if (snapshot.Error is not null)
                {
                    continue;
                }

                var provider = ProviderCatalog.NormalizeId(snapshot.Provider);
                foreach (var window in snapshot.Windows)
                {
                    if (window.PercentUsed is not double percent || double.IsNaN(percent) || double.IsInfinity(percent))
                    {
                        continue;
                    }

                    percent = Math.Clamp(percent, 0D, 1D);
                    var previous = points.LastOrDefault(point =>
                        string.Equals(point.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(point.Window, window.Id, StringComparison.OrdinalIgnoreCase));
                    if (previous is not null && sampleTime - previous.Timestamp < TimeSpan.FromSeconds(30))
                    {
                        points.Remove(previous);
                    }

                    points.Add(new UsageHistoryPoint(provider, window.Id, sampleTime, percent));
                }
            }

            var cutoff = sampleTime - Retention;
            points = points
                .Where(point => point.Timestamp >= cutoff)
                .GroupBy(point => $"{point.Provider}\u001f{point.Window}", StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group.OrderBy(point => point.Timestamp).TakeLast(MaxPointsPerSeries))
                .OrderBy(point => point.Timestamp)
                .ToList();

            await WriteCoreAsync(points, cancellationToken).ConfigureAwait(false);
            return points;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<UsageHistoryPoint>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        await using var stream = File.OpenRead(_path);
        var document = await JsonSerializer.DeserializeAsync<HistoryDocument>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
        return document?.Points ?? [];
    }

    private async Task WriteCoreAsync(IReadOnlyList<UsageHistoryPoint> points, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, new HistoryDocument { Version = Version, Points = points.ToList() },
                    SerializerOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed class HistoryDocument
    {
        public int Version { get; set; } = UsageHistoryStore.Version;
        public List<UsageHistoryPoint> Points { get; set; } = [];
    }
}
