using System.Text.RegularExpressions;

namespace Hmy.Estao.Core.Harnesses.Installer;

internal static partial class SourceRevision
{
    [GeneratedRegex("^[a-f0-9]{40}$", RegexOptions.CultureInvariant)]
    internal static partial Regex Sha();
}
