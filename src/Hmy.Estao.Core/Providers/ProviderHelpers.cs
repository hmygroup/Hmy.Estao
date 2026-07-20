using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Core.Providers;

internal static partial class ProviderHelpers
{
    public static ProviderSource ParseSource(string? source)
    {
        return source?.Trim().ToLowerInvariant() switch
        {
            "web" => ProviderSource.Web,
            "cli" => ProviderSource.Cli,
            "oauth" => ProviderSource.OAuth,
            "api" => ProviderSource.Api,
            _ => ProviderSource.Auto
        };
    }

    public static CookieSource ParseCookieSource(string? source)
    {
        return source?.Trim().ToLowerInvariant() switch
        {
            "manual" => CookieSource.Manual,
            "off" => CookieSource.Off,
            _ => CookieSource.Auto
        };
    }

    public static IReadOnlyList<ProviderAccount> TokenAccounts(ProviderConfig config)
    {
        return config.TokenAccounts?.Accounts
            .Select((account, index) => new ProviderAccount(
                string.IsNullOrWhiteSpace(account.Id) ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : account.Id!,
                string.IsNullOrWhiteSpace(account.Label) ? $"Account {index + 1}" : account.Label!,
                account.Token,
                WorkspaceId: account.WorkspaceId))
            .ToList() ?? [];
    }

    public static string? FirstString(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                {
                    return property.GetString();
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var value = FirstString(property.Value, names);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var value = FirstString(item, names);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    public static double? FirstNumber(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && TryGetNumber(property, out var number))
            {
                return number;
            }
        }

        return null;
    }

    public static bool TryGetNumber(JsonElement element, out double number)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out number))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
        {
            return true;
        }

        number = 0;
        return false;
    }

    public static DateTimeOffset? ResetFrom(JsonElement element)
    {
        var text = FirstString(element, "resetAt", "reset_at", "resetsAt", "resets_at", "endTime", "end_time");
        if (DateTimeOffset.TryParse(text, out var parsed))
        {
            return parsed;
        }

        var seconds = FirstNumber(element, "resetInSec", "reset_in_seconds", "resetSeconds", "secondsUntilReset");
        return seconds is null ? null : DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, seconds.Value));
    }

    public static double? PercentUsedFrom(JsonElement element, bool percentRemaining = false)
    {
        var direct = FirstNumber(element, "percentUsed", "usedPercent", "usagePercent", "usage_percent", "used_percent", "percent_used");
        if (direct is not null)
        {
            return NormalizePercent(direct.Value);
        }

        var remaining = FirstNumber(element, "percentRemaining", "remainingPercent", "percent_remaining", "remaining_percent");
        if (remaining is not null)
        {
            var normalized = NormalizePercent(remaining.Value);
            return normalized is null ? null : 1 - normalized;
        }

        var used = FirstNumber(element, "used", "current", "consumed");
        var limit = FirstNumber(element, "limit", "total", "maximum");
        if (used is not null && limit is > 0)
        {
            return Math.Clamp(used.Value / limit.Value, 0, 1);
        }

        return percentRemaining ? 1 : null;
    }

    public static double? NormalizePercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return null;
        }

        return Math.Clamp(value > 1 ? value / 100 : value, 0, 1);
    }

    public static JsonElement? FindProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var property))
                {
                    return property;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var found = FindProperty(property.Value, names);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindProperty(item, names);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    public static async Task<string> RunProcessAsync(string fileName, string arguments, string? workingDirectory, IReadOnlyDictionary<string, string?> environment, string? stdin, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var (key, value) in environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(key);
            }
            else
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = Process.Start(startInfo) ?? throw new ProviderException($"Could not start {fileName}.");
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new ProviderException(string.IsNullOrWhiteSpace(error) ? $"{fileName} exited with {process.ExitCode}." : error.Trim());
        }

        return output;
    }

    public static string UserHome() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ExpandHome(string path) => EstaoPaths.ExpandHome(path);

    [GeneratedRegex("\"(?<name>rollingUsage|weeklyUsage)\"\\s*:\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline)]
    public static partial Regex OpenCodeWindowRegex();
}
