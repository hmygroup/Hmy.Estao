using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Desktop;

internal static class DesktopLocalization
{
    private static readonly Regex Placeholder = new(@"\{(appName|\d+)\}", RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, Dictionary<string, string>> Catalogs = new[] { "en", "es", "fr" }.ToDictionary(id => id, id =>
    {
        using var stream = typeof(DesktopLocalization).Assembly.GetManifestResourceStream($"Localization.{id}.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    });
    public static string Language { get; private set; } = Resolve("system");
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    public static bool IsSupported(string value) => value is "system" or "en" or "es" or "fr";
    public static string Preference(string? value) => value is null ? "system" : IsSupported(value) ? value : Resolve(value);
    internal static string Resolve(string? value, string? system = null)
    {
        var name = value is null or "system" ? system ?? CultureInfo.CurrentUICulture.Name : value;
        var code = name.ToLowerInvariant().Split('-', '_')[0];
        return Catalogs.ContainsKey(code) ? code : "en";
    }
    public static void SetLanguage(string value) => Language = Resolve(value);
    public static string Text(string code, params object?[] args)
    {
        var template = Catalogs[Language].GetValueOrDefault(code) ?? Catalogs["en"].GetValueOrDefault(code) ?? Catalogs[Language]["error.operation"];
        return Placeholder.Replace(template, match =>
        {
            if (match.Groups[1].Value == "appName") return DesktopBranding.DisplayName;
            var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (index >= args.Length) return match.Value;
            return args[index] switch
            {
                UserMessage message => Format(message),
                IFormattable value => value.ToString(null, Culture),
                { } value => value.ToString() ?? "",
                _ => ""
            };
        });
    }
    public static string Format(UserMessage message) => message.Code == "message.list"
        ? string.Join(Environment.NewLine, message.Arguments.OfType<UserMessage>().Select(Format))
        : Text(message.Code, message.Arguments);
    public static string Error(Exception error) => Format(UserMessage.FromException(error));
}
