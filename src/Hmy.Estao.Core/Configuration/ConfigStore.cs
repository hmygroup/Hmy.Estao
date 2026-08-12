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
            Theme = "Graphite",
            BackdropStyle = "None",
            TaskbarOverlay = new TaskbarOverlayConfig
            {
                Enabled = true,
                ProviderIds = [],
                Controls = TaskbarOverlayControlCatalog.Default.ToList()
            },
            Providers = ProviderCatalog.InitialProviderIds
                .Select(id => new ProviderConfig { Id = id, Enabled = id == "codex", Source = "auto", CookieSource = "auto" })
                .ToList()
        };
    }

    public static EstaoConfig Normalize(EstaoConfig config)
    {
        config.Version = config.Version <= 0 ? 1 : config.Version;
        config.Theme = string.IsNullOrWhiteSpace(config.Theme) ? "Graphite" : config.Theme.Trim();
        config.BackdropStyle = NormalizeBackdropStyle(config.BackdropStyle);
        config.Providers ??= [];
        config.TaskbarOverlay ??= new TaskbarOverlayConfig();
        config.Refresh ??= new RefreshConfig();
        config.Refresh.IntervalMinutes = RefreshIntervalCatalog.Minutes.Contains(config.Refresh.IntervalMinutes)
            ? config.Refresh.IntervalMinutes
            : 15;
        var legacyPacing = config.LegacyPacing;
        config.TaskbarOverlay.ProviderIds ??= [];
        config.TaskbarOverlay.Controls ??= [];
        config.TaskbarOverlay.DisplayMode = NormalizeOverlayValue(
            config.TaskbarOverlay.DisplayMode, TaskbarOverlayDisplayCatalog.DisplayModes, "icon-title");
        config.TaskbarOverlay.Size = NormalizeOverlayValue(
            config.TaskbarOverlay.Size, TaskbarOverlayDisplayCatalog.Sizes, "normal");
        if (config.TaskbarOverlay.PositionX is null || config.TaskbarOverlay.PositionY is null)
        {
            config.TaskbarOverlay.PositionX = null;
            config.TaskbarOverlay.PositionY = null;
        }
        config.TaskbarOverlay.ProviderIds = config.TaskbarOverlay.ProviderIds
            .Select(ProviderCatalog.NormalizeId)
            .Where(ProviderCatalog.IsSupported)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.TaskbarOverlay.Controls = config.TaskbarOverlay.Controls
            .Select(control => control.Trim())
            .Where(control => TaskbarOverlayControlCatalog.All.Contains(control, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (config.TaskbarOverlay.Controls.Count == 0)
            config.TaskbarOverlay.Controls = TaskbarOverlayControlCatalog.Default.ToList();

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

        foreach (var provider in config.Providers)
        {
            if (legacyPacing is not null && !provider.HasExplicitPacing)
                provider.Pacing = CopyPacing(legacyPacing);
            NormalizePacing(provider.Pacing);
            NormalizeUsageColors(provider.UsageColors);
        }

        // A legacy global setting is copied to every existing provider once.
        // Clearing it means the next save emits only the provider-level shape.
        config.LegacyPacing = null;

        return config;
    }

    private static void NormalizePacing(PacingConfig pacing)
    {
        pacing.DailyTargetPercent = Math.Clamp(
            pacing.DailyTargetPercent <= 0 ? 15D : pacing.DailyTargetPercent,
            PacingCatalog.MinDailyTargetPercent, PacingCatalog.MaxDailyTargetPercent);
    }

    private static void NormalizeUsageColors(UsageColorConfig colors)
    {
        colors.WarningPercent = Math.Clamp(
            double.IsFinite(colors.WarningPercent) ? colors.WarningPercent : UsageColorCatalog.DefaultWarningPercent,
            0D, UsageColorCatalog.MaximumWarningPercent);
        colors.CriticalPercent = Math.Clamp(
            double.IsFinite(colors.CriticalPercent) ? colors.CriticalPercent : UsageColorCatalog.DefaultCriticalPercent,
            colors.WarningPercent + 1D, 100D);
        colors.WarningColor = UsageColorCatalog.NormalizeColor(
            colors.WarningColor, UsageColorCatalog.DefaultWarningColor);
        colors.CriticalColor = UsageColorCatalog.NormalizeColor(
            colors.CriticalColor, UsageColorCatalog.DefaultCriticalColor);
    }

    private static PacingConfig CopyPacing(PacingConfig pacing) => new()
    {
        Enabled = pacing.Enabled,
        DailyTargetPercent = pacing.DailyTargetPercent,
        NotifyOnExceed = pacing.NotifyOnExceed
    };

    private static string NormalizeBackdropStyle(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "mica" => "Mica",
            "acrylic" => "Acrylic",
            _ => "None"
        };
    }

    private static string NormalizeEnumValue(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    }

    private static string NormalizeOverlayValue(string? value, IReadOnlyCollection<string> allowed, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
        return allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? normalized : fallback;
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
