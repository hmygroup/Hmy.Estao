using Hmy.Estao.Core;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Formatting;
using Hmy.Estao.Core.Refresh;
using Hmy.Estao.Core.Security;

return await Cli.RunAsync(args).ConfigureAwait(false);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var command = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal)) ?? "usage";
            return command switch
            {
                "usage" => await UsageAsync(args).ConfigureAwait(false),
                "config" => await ConfigAsync(args.SkipWhile(arg => arg != "config").Skip(1).ToArray()).ConfigureAwait(false),
                "--help" or "-h" or "help" => Help(),
                "--version" or "-V" => Version(),
                _ => Error($"Unknown command '{command}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> UsageAsync(string[] args)
    {
        var format = Option(args, "--format") ?? "text";
        var pretty = args.Contains("--pretty", StringComparer.OrdinalIgnoreCase);
        var provider = Option(args, "--provider");
        var accountIndex = Option(args, "--account-index");
        if (args.Contains("--allow-browser-import", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Automatic browser cookie import was removed. Use 'estao config set-cookie --provider <name> --stdin' to save a cookie securely.");
        }

        var store = new ConfigStore();
        if (!string.IsNullOrWhiteSpace(provider) || !string.IsNullOrWhiteSpace(accountIndex))
        {
            var config = await store.LoadAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(provider))
            {
                foreach (var item in config.Providers)
                {
                    item.Enabled = string.Equals(item.Id, ProviderCatalog.NormalizeId(provider), StringComparison.Ordinal);
                    if (item.Enabled == true && int.TryParse(accountIndex, out var parsedIndex))
                    {
                        item.ActiveAccountIndex = Math.Max(0, parsedIndex - 1);
                    }
                }
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"estao-config-{Guid.NewGuid():N}.json");
            var tempStore = new ConfigStore(tempPath);
            await tempStore.SaveAsync(config).ConfigureAwait(false);
            store = tempStore;
        }

        var service = new UsageRefreshService(store);
        var snapshots = await service.RefreshAsync().ConfigureAwait(false);
        Console.WriteLine(format.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? UsageFormatter.ToJson(snapshots, pretty)
            : UsageFormatter.ToText(snapshots));

        return snapshots.Any(snapshot => snapshot.Error is not null) ? 1 : 0;
    }

    private static async Task<int> ConfigAsync(string[] args)
    {
        var store = new ConfigStore();
        var subcommand = args.FirstOrDefault() ?? "validate";
        switch (subcommand)
        {
            case "validate":
            {
                var config = await store.LoadAsync().ConfigureAwait(false);
                var errors = ConfigValidation.Validate(config);
                if (errors.Count == 0)
                {
                    Console.WriteLine($"Config valid: {store.Path}");
                    return 0;
                }

                foreach (var error in errors)
                {
                    Console.Error.WriteLine(error);
                }

                return 1;
            }

            case "dump":
            {
                var config = await store.LoadAsync().ConfigureAwait(false);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
                return 0;
            }

            case "enable":
            case "disable":
            {
                var provider = RequireOption(args, "--provider");
                var config = await store.LoadAsync().ConfigureAwait(false);
                var providerConfig = FindOrCreate(config, provider);
                providerConfig.Enabled = subcommand == "enable";
                await store.SaveAsync(config).ConfigureAwait(false);
                Console.WriteLine($"{ProviderCatalog.DisplayName(providerConfig.Id)} {(providerConfig.Enabled == true ? "enabled" : "disabled")}");
                return 0;
            }

            case "set-api-key":
            case "set":
            {
                var provider = RequireOption(args, "--provider");
                var value = args.Contains("--stdin", StringComparer.OrdinalIgnoreCase)
                    ? await Console.In.ReadToEndAsync().ConfigureAwait(false)
                    : Option(args, "--api-key") ?? Option(args, "--value") ?? throw new InvalidOperationException("Use --stdin, --api-key, or --value.");
                var config = await store.LoadAsync().ConfigureAwait(false);
                var providerConfig = FindOrCreate(config, provider);
                providerConfig.ApiKey = value.Trim();
                providerConfig.Enabled ??= true;
                await store.SaveAsync(config).ConfigureAwait(false);
                Console.WriteLine($"Saved token for {ProviderCatalog.DisplayName(providerConfig.Id)} in raw JSON config.");
                return 0;
            }

            case "set-cookie":
            {
                var provider = RequireOption(args, "--provider");
                var value = args.Contains("--stdin", StringComparer.OrdinalIgnoreCase)
                    ? await Console.In.ReadToEndAsync().ConfigureAwait(false)
                    : Option(args, "--cookie") ?? Option(args, "--value") ?? throw new InvalidOperationException("Use --stdin, --cookie, or --value.");
                var config = await store.LoadAsync().ConfigureAwait(false);
                var providerConfig = FindOrCreate(config, provider);
                await new SecureCookieStore().SaveCookieHeaderAsync(providerConfig.Id, value).ConfigureAwait(false);
                providerConfig.CookieSource = "manual";
                providerConfig.CookieHeader = null;
                providerConfig.Enabled ??= true;
                await store.SaveAsync(config).ConfigureAwait(false);
                Console.WriteLine($"Saved encrypted cookie for {ProviderCatalog.DisplayName(providerConfig.Id)}.");
                return 0;
            }

            case "clear-cookie":
            {
                var provider = RequireOption(args, "--provider");
                var config = await store.LoadAsync().ConfigureAwait(false);
                var providerConfig = FindOrCreate(config, provider);
                await new SecureCookieStore().ClearCookieHeaderAsync(providerConfig.Id).ConfigureAwait(false);
                providerConfig.CookieHeader = null;
                await store.SaveAsync(config).ConfigureAwait(false);
                Console.WriteLine($"Cleared saved cookie for {ProviderCatalog.DisplayName(providerConfig.Id)}.");
                return 0;
            }

            case "import":
            {
                var source = RequireOption(args, "--file");
                await store.ImportExplicitAsync(source).ConfigureAwait(false);
                Console.WriteLine($"Imported config to {store.Path}");
                return 0;
            }

            default:
                return Error($"Unknown config command '{subcommand}'.");
        }
    }

    private static ProviderConfig FindOrCreate(EstaoConfig config, string provider)
    {
        var id = ProviderCatalog.NormalizeId(provider);
        if (!ProviderCatalog.IsSupported(id))
        {
            throw new InvalidOperationException($"Provider '{provider}' is not supported by Estao MVP.");
        }

        var providerConfig = config.Providers.FirstOrDefault(item => item.Id == id);
        if (providerConfig is not null)
        {
            return providerConfig;
        }

        providerConfig = new ProviderConfig { Id = id, Source = "auto", CookieSource = "auto" };
        config.Providers.Add(providerConfig);
        return providerConfig;
    }

    private static string? Option(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string RequireOption(string[] args, string name)
    {
        return Option(args, name) ?? throw new InvalidOperationException($"Missing required option {name}.");
    }

    private static int Help()
    {
        Console.WriteLine($"{EstaoConstants.CliName} usage [--provider codex|claude|copilot|opencode] [--format text|json] [--pretty]");
        Console.WriteLine($"{EstaoConstants.CliName} config validate|dump|enable|disable|set-api-key|set-cookie|clear-cookie|import");
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine(typeof(Cli).Assembly.GetName().Version?.ToString() ?? "0.1.0");
        return 0;
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 64;
    }
}
