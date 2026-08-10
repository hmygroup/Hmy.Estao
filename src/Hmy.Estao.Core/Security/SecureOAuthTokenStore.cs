using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Core.Security;

public sealed record OAuthToken(string AccessToken, string? AccountLabel);

public interface IOAuthTokenStore
{
    Task<OAuthToken?> ReadAsync(string providerId, CancellationToken cancellationToken = default);

    Task SaveAsync(string providerId, OAuthToken token, CancellationToken cancellationToken = default);
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

    public SecureOAuthTokenStore(string? path = null, ISecretProtector? protector = null)
    {
        _path = path ?? Path.Combine(EstaoPaths.ResolveDataDirectory(), SecretsFileName);
        _protector = protector ?? new DpapiSecretProtector();
    }

    public async Task<OAuthToken?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!file.Tokens.TryGetValue(NormalizeProviderId(providerId), out var entry) || string.IsNullOrWhiteSpace(entry.ProtectedToken))
        {
            return null;
        }

        var protectedBytes = Convert.FromBase64String(entry.ProtectedToken);
        var plaintext = _protector.Unprotect(protectedBytes);
        var token = Encoding.UTF8.GetString(plaintext);
        return string.IsNullOrWhiteSpace(token) ? null : new OAuthToken(token, entry.AccountLabel);
    }

    public async Task SaveAsync(string providerId, OAuthToken token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new ArgumentException("OAuth access token cannot be empty.", nameof(token));
        }

        var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var plaintext = Encoding.UTF8.GetBytes(token.AccessToken.Trim());
        file.Tokens[NormalizeProviderId(providerId)] = new OAuthTokenEntry
        {
            ProtectedToken = Convert.ToBase64String(_protector.Protect(plaintext)),
            AccountLabel = string.IsNullOrWhiteSpace(token.AccountLabel) ? null : token.AccountLabel.Trim()
        };
        await SaveAsync(file, cancellationToken).ConfigureAwait(false);
    }

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
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, file, SerializerOptions, cancellationToken).ConfigureAwait(false);
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
    }
}
