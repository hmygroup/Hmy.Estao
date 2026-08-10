using System.Text.Json;

namespace Hmy.Estao.Core.Configuration;

/// <summary>
/// Tiny piece of runtime bookkeeping (NOT user-editable settings, so it lives
/// outside <see cref="ConfigStore"/>/<see cref="EstaoConfig"/>) that remembers
/// the last day Estao already warned about a provider/window going over its
/// daily pacing target, so the tray only nags once per day per window.
/// </summary>
public sealed class PacingStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PacingStateStore(string configPath)
    {
        var directory = System.IO.Path.GetDirectoryName(configPath);
        _path = System.IO.Path.Combine(string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory,
            "pacing-state.json");
    }

    public string Path => _path;

    /// <summary>
    /// Returns true and records today's date the first time a given
    /// provider/window crosses its pacing target on a given day; returns
    /// false on subsequent calls for the same day so callers only notify once.
    /// </summary>
    public async Task<bool> TryMarkNotifiedAsync(string provider, string window, DateOnly today,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var key = Key(provider, window);
            if (state.Entries.TryGetValue(key, out var lastNotified) && lastNotified == today)
            {
                return false;
            }

            state.Entries[key] = today;
            await WriteCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Key(string provider, string window) => $"{provider}\u001f{window}".ToLowerInvariant();

    private async Task<StateDocument> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new StateDocument();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync<StateDocument>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            return document ?? new StateDocument();
        }
        catch (JsonException)
        {
            return new StateDocument();
        }
    }

    private async Task WriteCoreAsync(StateDocument state, CancellationToken cancellationToken)
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
                await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken).ConfigureAwait(false);
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

    private sealed class StateDocument
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, DateOnly> Entries { get; set; } = [];
    }
}
