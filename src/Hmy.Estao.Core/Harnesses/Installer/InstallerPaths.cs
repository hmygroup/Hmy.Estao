using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Hmy.Estao.Core.Harnesses.Installer;

public static partial class InstallerPaths
{
    public static void EnsurePrivateDirectory(string directory)
    {
        CheckLocal(directory);
        Directory.CreateDirectory(directory);
        CheckLocal(directory);
        if (OperatingSystem.IsWindows()) RestrictWindowsDirectory(directory);
    }
    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsDirectory(string directory)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("No Windows user identity.");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
    public static string Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || value.Contains('\\') || value.StartsWith('/'))
            throw new InvalidDataException("Unsafe relative path.");
        foreach (var part in value.Split('/'))
            ValidateComponent(part);
        return value;
    }

    private static void ValidateComponent(string part)
    {
        if (part is "" or "." or ".." || part.Length > 255 || part.EndsWith('.') || part.EndsWith(' ') ||
            part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || Reserved().IsMatch(part.Split('.')[0]))
            throw new InvalidDataException("Unsafe Windows path component.");
    }

    public static string Within(string root, string relative)
    {
        Relative(relative);
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Destination escapes profile.");
        CheckLocal(target);
        return target;
    }

    // Check every ancestor, including ancestors of an overridden profile or private data directory.
    public static void CheckLocal(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("Network and device profiles are not supported.");
        var root = Path.GetPathRoot(path)!;
        var relative = path[root.Length..].Replace('\\', '/');
        if (relative.Length > 0) foreach (var part in relative.Split('/')) ValidateComponent(part);
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Installation paths must not traverse symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string? FileHash(string path)
    {
        CheckLocal(path);
        if (Directory.Exists(path)) throw new IOException("A directory occupies a file destination.");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("Destination exceeds the safe read limit.");
        return Hash(File.ReadAllBytes(path));
    }
    public static string GitBlob(byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"));
        hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex Reserved();
}
