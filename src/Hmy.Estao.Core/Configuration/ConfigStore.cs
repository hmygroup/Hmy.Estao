using System.Text.Json;
using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Core.Configuration;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _path;

    public ConfigStore(string? path = null)
    {
        _path = path ?? EstaoPaths.ResolveConfigPath();
    }

    public string Path => _path;

    public async Task<EstaoConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return CreateDefaultConfig();
        }

        await using var stream = File.OpenRead(_path);
        var config = await JsonSerializer.DeserializeAsync<EstaoConfig>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        return Normalize(config ?? CreateDefaultConfig());
    }

    public async Task SaveAsync(EstaoConfig config, CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, Normalize(config), SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportExplicitAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Config file was not found.", sourcePath);
        }

        await using var source = File.OpenRead(sourcePath);
        var config = await JsonSerializer.DeserializeAsync<EstaoConfig>(source, SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Config file is empty or invalid JSON.");

        await SaveAsync(config, cancellationToken).ConfigureAwait(false);
    }

    public static EstaoConfig CreateDefaultConfig()
    {
        return new EstaoConfig
        {
            Version = 1,
            Providers = ProviderCatalog.InitialProviderIds
                .Select(id => new ProviderConfig { Id = id, Enabled = false })
                .ToList()
        };
    }

    public static EstaoConfig Normalize(EstaoConfig config)
    {
        config.Version = config.Version <= 0 ? 1 : config.Version;
        config.Providers ??= [];

        foreach (var provider in config.Providers)
        {
            provider.Id = ProviderCatalog.NormalizeId(provider.Id);
            provider.Source = NormalizeEnumValue(provider.Source, "auto");
            provider.CookieSource = NormalizeEnumValue(provider.CookieSource, "auto");
        }

        foreach (var id in ProviderCatalog.InitialProviderIds)
        {
            if (config.Providers.All(provider => !string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                config.Providers.Add(new ProviderConfig { Id = id, Enabled = false, Source = "auto", CookieSource = "auto" });
            }
        }

        return config;
    }

    private static string NormalizeEnumValue(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    }
}

public static class ConfigValidation
{
    public static IReadOnlyList<string> Validate(EstaoConfig config)
    {
        var errors = new List<string>();
        if (config.Version != 1)
        {
            errors.Add($"Unsupported config version {config.Version}; only version 1 is supported.");
        }

        foreach (var provider in config.Providers)
        {
            if (!ProviderCatalog.IsSupported(provider.Id))
            {
                errors.Add($"Unsupported provider '{provider.Id}'.");
            }

            if (!IsValid(provider.Source, new HashSet<string>(StringComparer.Ordinal) { "auto", "web", "cli", "oauth", "api" }))
            {
                errors.Add($"Provider '{provider.Id}' has invalid source '{provider.Source}'.");
            }

            if (!IsValid(provider.CookieSource, new HashSet<string>(StringComparer.Ordinal) { "auto", "manual", "off" }))
            {
                errors.Add($"Provider '{provider.Id}' has invalid cookieSource '{provider.CookieSource}'.");
            }
        }

        return errors;
    }

    private static bool IsValid(string? value, IReadOnlySet<string> allowed)
    {
        return value is null || allowed.Contains(value.Trim().ToLowerInvariant());
    }
}
