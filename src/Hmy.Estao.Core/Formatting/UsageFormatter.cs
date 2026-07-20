using System.Text;
using System.Text.Json;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Core.Formatting;

public static class UsageFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string ToJson(IEnumerable<UsageSnapshot> snapshots, bool pretty)
    {
        var options = pretty ? JsonOptions : new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return JsonSerializer.Serialize(snapshots, options);
    }

    public static string ToText(IEnumerable<UsageSnapshot> snapshots)
    {
        var builder = new StringBuilder();
        foreach (var snapshot in snapshots)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine($"== {snapshot.DisplayName} ({snapshot.Source}) ==");
            if (!string.IsNullOrWhiteSpace(snapshot.Error))
            {
                builder.AppendLine($"Error: {snapshot.Error}");
                continue;
            }

            foreach (var window in snapshot.Windows)
            {
                var remaining = window.PercentRemaining is null ? "unknown" : $"{window.PercentRemaining.Value:P0} left";
                builder.AppendLine($"{window.Title}: {remaining}");
                if (window.ResetAt is not null)
                {
                    builder.AppendLine($"Resets: {window.ResetAt.Value.LocalDateTime:g}");
                }
            }

            if (snapshot.Credits?.Balance is not null)
            {
                builder.AppendLine($"Credits: {snapshot.Credits.Balance:0.##} {snapshot.Credits.Unit}".TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(snapshot.Account))
            {
                builder.AppendLine($"Account: {snapshot.Account}");
            }

            if (!string.IsNullOrWhiteSpace(snapshot.Plan))
            {
                builder.AppendLine($"Plan: {snapshot.Plan}");
            }
        }

        return builder.ToString().TrimEnd();
    }
}
