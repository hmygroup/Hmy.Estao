using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Core.Security;

public interface ICookieSecretStore
{
    Task<string?> ReadCookieHeaderAsync(string providerId, CancellationToken cancellationToken = default);

    Task SaveCookieHeaderAsync(string providerId, string cookieHeader, CancellationToken cancellationToken = default);

    Task ClearCookieHeaderAsync(string providerId, CancellationToken cancellationToken = default);
}

public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedData);
}

public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI cookie storage is only supported on Windows.");
        }

        return ProtectWindows(plaintext);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI cookie storage is only supported on Windows.");
        }

        return UnprotectWindows(protectedData);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] plaintext)
    {
        return ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] protectedData)
    {
        return ProtectedData.Unprotect(protectedData, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }
}

public sealed class SecureCookieStore : ICookieSecretStore
{
    private const int CurrentVersion = 1;
    private const string SecretsFileName = "cookie-secrets.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _path;
    private readonly ISecretProtector _protector;

    public SecureCookieStore(string? path = null, ISecretProtector? protector = null)
    {
        _path = path ?? Path.Combine(EstaoPaths.ResolveDataDirectory(), SecretsFileName);
        _protector = protector ?? new DpapiSecretProtector();
    }

    public async Task<string?> ReadCookieHeaderAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var id = NormalizeProviderId(providerId);
        var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!file.Cookies.TryGetValue(id, out var protectedText) || string.IsNullOrWhiteSpace(protectedText))
        {
            return null;
        }

        var protectedBytes = Convert.FromBase64String(protectedText);
        var plaintext = _protector.Unprotect(protectedBytes);
        return Encoding.UTF8.GetString(plaintext);
    }

    public async Task SaveCookieHeaderAsync(string providerId, string cookieHeader, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            throw new ArgumentException("Cookie header cannot be empty.", nameof(cookieHeader));
        }

        var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var plaintext = Encoding.UTF8.GetBytes(NormalizeCookieHeader(cookieHeader));
        file.Cookies[NormalizeProviderId(providerId)] = Convert.ToBase64String(_protector.Protect(plaintext));
        await SaveAsync(file, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearCookieHeaderAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return;
        }

        var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!file.Cookies.Remove(NormalizeProviderId(providerId)))
        {
            return;
        }

        if (file.Cookies.Count == 0)
        {
            File.Delete(_path);
            return;
        }

        await SaveAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CookieSecretsFile> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new CookieSecretsFile();
        }

        await using var stream = File.OpenRead(_path);
        var file = await JsonSerializer.DeserializeAsync<CookieSecretsFile>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? new CookieSecretsFile();

        return new CookieSecretsFile
        {
            Version = file.Version <= 0 ? CurrentVersion : file.Version,
            Cookies = new Dictionary<string, string>(file.Cookies, StringComparer.Ordinal)
        };
    }

    private async Task SaveAsync(CookieSecretsFile file, CancellationToken cancellationToken)
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

    private static string NormalizeCookieHeader(string cookieHeader)
    {
        return cookieHeader.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)
            ? cookieHeader[7..].Trim()
            : cookieHeader.Trim();
    }

    private sealed class CookieSecretsFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("cookies")]
        public Dictionary<string, string> Cookies { get; set; } = new(StringComparer.Ordinal);
    }
}
