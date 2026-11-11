using System.Text;

namespace Hmy.Estao.Core.Harnesses.Installer;

public static class InstructionDocument
{
    public static (SkillMetadataInfo? Metadata, string Body) Read(byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return (null, text);
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0 && text.EndsWith("\n---", StringComparison.Ordinal)) end = text.Length - 4;
        if (end < 0 || end > 32768) throw new InvalidDataException("Invalid instruction frontmatter.");
        var metadata = HarnessAdapter.ReadSkillMetadata(Encoding.UTF8.GetBytes(text));
        if (metadata.Description.Length > 1024) throw new InvalidDataException("Instruction descriptions must be at most 1024 characters.");
        return (metadata, text[Math.Min(end + 5, text.Length)..]);
    }
}
