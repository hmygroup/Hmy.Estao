using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Core.Security;

public sealed record OAuthToken(string AccessToken, string? AccountLabel, string? RefreshToken = null,
    DateTimeOffset? ExpiresAt = null, string? Scope = null);

public interface IOAuthTokenStore
{
    Task<OAuthToken?> ReadAsync(string providerId, CancellationToken cancellationToken = default);

    Task SaveAsync(string providerId, OAuthToken token, CancellationToken cancellationToken = default);

    async Task<bool> TryReplaceAsync(string providerId, OAuthToken expected, OAuthToken replacement, CancellationToken cancellationToken = default)
    {
        if (await ReadAsync(providerId, cancellationToken).ConfigureAwait(false) != expected) return false;
        await SaveAsync(providerId, replacement, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Stores OAuth access tokens encrypted with Windows DPAPI for the current user.</summary>
public sealed class SecureOAuthTokenStore : IOAuthTokenStore
{
    private const int CurrentVersion = 1;
    private const string SecretsFileName = "oauth-secrets.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;

    public SecureOAuthTokenStore(string? path = null, ISecretProtector? protector = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(EstaoPaths.ResolveDataDirectory(), SecretsFileName));
        _protector = protector ?? new DpapiSecretProtector();
        _gate = Gates.GetOrAdd(_path, _ => new(1, 1));
    }

    public async Task<OAuthToken?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return file.Tokens.TryGetValue(NormalizeProviderId(providerId), out var entry) ? ReadEntry(entry) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string providerId, OAuthToken token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new ArgumentException("OAuth access token cannot be empty.", nameof(token));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
            file.Tokens[NormalizeProviderId(providerId)] = ProtectEntry(token);
            await SaveAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(string providerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return;
            var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (file.Tokens.Remove(NormalizeProviderId(providerId)))
                await SaveAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> TryReplaceAsync(string providerId, OAuthToken expected, OAuthToken replacement, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement.AccessToken);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var id = NormalizeProviderId(providerId);
            // A late refresh must not resurrect sign-out or overwrite a new login.
            if (!file.Tokens.TryGetValue(id, out var entry) || ReadEntry(entry) != expected) return false;
            file.Tokens[id] = ProtectEntry(replacement);
            await SaveAsync(file, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    private OAuthToken? ReadEntry(OAuthTokenEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ProtectedToken)) return null;
        var token = Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(entry.ProtectedToken)));
        var refresh = string.IsNullOrWhiteSpace(entry.ProtectedRefreshToken) ? null
            : Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(entry.ProtectedRefreshToken)));
        return string.IsNullOrWhiteSpace(token) ? null : new(token, entry.AccountLabel, refresh, entry.ExpiresAt, entry.Scope);
    }

    private OAuthTokenEntry ProtectEntry(OAuthToken token) => new()
    {
        ProtectedToken = Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(token.AccessToken.Trim()))),
        ProtectedRefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? null
            : Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(token.RefreshToken))),
        ExpiresAt = token.ExpiresAt, Scope = token.Scope,
        AccountLabel = string.IsNullOrWhiteSpace(token.AccountLabel) ? null : token.AccountLabel.Trim()
    };

    private async Task<OAuthSecretsFile> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new OAuthSecretsFile();
        }

        await using var stream = File.OpenRead(_path);
        var file = await JsonSerializer.DeserializeAsync<OAuthSecretsFile>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? new OAuthSecretsFile();
        file.Tokens ??= new Dictionary<string, OAuthTokenEntry>(StringComparer.Ordinal);
        return file;
    }

    private async Task SaveAsync(OAuthSecretsFile file, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        file.Version = CurrentVersion;
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, file, SerializerOptions, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string NormalizeProviderId(string providerId) => ProviderCatalog.NormalizeId(providerId);

    private sealed class OAuthSecretsFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("tokens")]
        public Dictionary<string, OAuthTokenEntry> Tokens { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class OAuthTokenEntry
    {
        [JsonPropertyName("protectedToken")]
        public string? ProtectedToken { get; set; }

        [JsonPropertyName("accountLabel")]
        public string? AccountLabel { get; set; }
        public string? ProtectedRefreshToken { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public string? Scope { get; set; }
    }
}
