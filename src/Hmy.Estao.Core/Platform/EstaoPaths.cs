using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Core.Platform;

public static class EstaoPaths
{
    public static string ResolveConfigPath(IEnvironment? environment = null)
    {
        environment ??= SystemEnvironment.Instance;
        var overridePath = environment.GetEnvironmentVariable(EstaoConstants.ConfigEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return ExpandHome(overridePath.Trim(), environment);
        }

        var appData = environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.Combine(environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
        }

        return Path.Combine(appData, EstaoConstants.AppDataFolderName, EstaoConstants.ConfigFileName);
    }

    public static string ResolveDataDirectory(IEnvironment? environment = null)
    {
        return Path.GetDirectoryName(ResolveConfigPath(environment)) ?? Environment.CurrentDirectory;
    }

    public static string ExpandHome(string path, IEnvironment? environment = null)
    {
        environment ??= SystemEnvironment.Instance;
        if (path == "~")
        {
            return environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return Environment.ExpandEnvironmentVariables(path);
    }
}

public interface IEnvironment
{
    string? GetEnvironmentVariable(string name);

    string GetFolderPath(Environment.SpecialFolder folder);
}

public sealed class SystemEnvironment : IEnvironment
{
    public static SystemEnvironment Instance { get; } = new();

    private SystemEnvironment()
    {
    }

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string GetFolderPath(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
}
