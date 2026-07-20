using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;

namespace Hmy.Estao.Core.Security;

public interface IBrowserCookieImporter
{
    Task<CookieImportResult> TryImportCookieHeaderAsync(string domain, IReadOnlyList<string> requiredNames, CancellationToken cancellationToken);
}

public sealed record CookieImportResult(string? CookieHeader, string? Source, string? Error)
{
    public static CookieImportResult Success(string header, string source) => new(header, source, null);

    public static CookieImportResult Failure(string error) => new(null, null, error);
}

public sealed class BrowserCookieImporter : IBrowserCookieImporter
{
    private static readonly string[] BrowserRoots = ["Google\\Chrome\\User Data", "Microsoft\\Edge\\User Data"];

    public Task<CookieImportResult> TryImportCookieHeaderAsync(string domain, IReadOnlyList<string> requiredNames, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(CookieImportResult.Failure("Automatic browser import is only supported on Windows."));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return Task.FromResult(CookieImportResult.Failure("LOCALAPPDATA is not available."));
        }

        var errors = new List<string>();
        foreach (var rootSuffix in BrowserRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.Combine(localAppData, rootSuffix);
            if (!Directory.Exists(root))
            {
                continue;
            }

            var key = TryGetChromiumKey(root, errors);
            foreach (var profile in EnumerateProfiles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = TryReadProfileCookies(profile, key, domain, requiredNames, errors);
                if (!string.IsNullOrWhiteSpace(result.CookieHeader))
                {
                    return Task.FromResult(result);
                }
            }
        }

        var suffix = errors.Count == 0 ? "No Chrome or Edge cookie store was found." : string.Join(" ", errors.Distinct());
        return Task.FromResult(CookieImportResult.Failure(suffix));
    }

    private static IEnumerable<string> EnumerateProfiles(string root)
    {
        var defaultProfile = Path.Combine(root, "Default");
        if (Directory.Exists(defaultProfile))
        {
            yield return defaultProfile;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "Profile *"))
        {
            yield return directory;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[]? TryGetChromiumKey(string root, List<string> errors)
    {
        var localStatePath = Path.Combine(root, "Local State");
        if (!File.Exists(localStatePath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(localStatePath));
            if (!document.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
                !osCrypt.TryGetProperty("encrypted_key", out var encryptedKeyElement) ||
                encryptedKeyElement.GetString() is not { Length: > 0 } encryptedKeyText)
            {
                return null;
            }

            var encryptedKey = Convert.FromBase64String(encryptedKeyText);
            var dpapiPrefix = Encoding.ASCII.GetBytes("DPAPI");
            if (encryptedKey.AsSpan().StartsWith(dpapiPrefix))
            {
                encryptedKey = encryptedKey[dpapiPrefix.Length..];
            }

            return ProtectedData.Unprotect(encryptedKey, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException)
        {
            errors.Add($"Could not read Chromium encryption key from {root}: {ex.Message}");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static CookieImportResult TryReadProfileCookies(string profile, byte[]? key, string domain, IReadOnlyList<string> requiredNames, List<string> errors)
    {
        var cookiePath = Path.Combine(profile, "Network", "Cookies");
        if (!File.Exists(cookiePath))
        {
            cookiePath = Path.Combine(profile, "Cookies");
        }

        if (!File.Exists(cookiePath))
        {
            return CookieImportResult.Failure("No cookie database in profile.");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"estao-cookies-{Guid.NewGuid():N}.sqlite");
        try
        {
            File.Copy(cookiePath, tempPath, overwrite: true);
            var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
            using var connection = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, value, encrypted_value FROM cookies WHERE host_key LIKE $domain ORDER BY last_access_utc DESC";
            command.Parameters.AddWithValue("$domain", "%" + domain.TrimStart('.'));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var plainValue = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var encryptedValue = reader.IsDBNull(2) ? [] : (byte[])reader[2];
                var value = !string.IsNullOrEmpty(plainValue) ? plainValue : TryDecryptCookie(encryptedValue, key);
                if (value is null)
                {
                    if (encryptedValue.AsSpan().StartsWith("v20"u8))
                    {
                        errors.Add("Some Chromium cookies use app-bound encryption and cannot be imported; use manual cookie setup.");
                    }

                    continue;
                }

                cookies.TryAdd(name, value);
            }

            if (requiredNames.Count > 0 && requiredNames.Any(name => !cookies.ContainsKey(name)))
            {
                return CookieImportResult.Failure("Required cookies were not found.");
            }

            if (cookies.Count == 0)
            {
                return CookieImportResult.Failure("No importable cookies were found.");
            }

            var header = string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}"));
            return CookieImportResult.Success(header, profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or CryptographicException)
        {
            errors.Add($"Could not import cookies from {Path.GetFileName(profile)}: {ex.Message}");
            return CookieImportResult.Failure(ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? TryDecryptCookie(byte[] encryptedValue, byte[]? key)
    {
        if (encryptedValue.Length == 0)
        {
            return string.Empty;
        }

        if (encryptedValue.AsSpan().StartsWith("v20"u8))
        {
            return null;
        }

        if ((encryptedValue.AsSpan().StartsWith("v10"u8) || encryptedValue.AsSpan().StartsWith("v11"u8)) && key is not null)
        {
            var nonce = encryptedValue.AsSpan(3, 12);
            var ciphertext = encryptedValue.AsSpan(15, encryptedValue.Length - 15 - 16);
            var tag = encryptedValue.AsSpan(encryptedValue.Length - 16, 16);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }

        var decrypted = ProtectedData.Unprotect(encryptedValue, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }
}
