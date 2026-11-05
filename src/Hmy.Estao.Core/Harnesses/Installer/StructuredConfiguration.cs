using System.Text;
using System.Text.Json;
using Tomlyn;

namespace Hmy.Estao.Core.Harnesses.Installer;

/// <summary>Conservative, lossless additions. Never regenerates an existing document.</summary>
public static class StructuredConfiguration
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 };

    public static IReadOnlyDictionary<string, string> NodeHashes(byte[] content)
    {
        Validate(content);
        using var document = JsonDocument.Parse(content, DocumentOptions);
        var result = new Dictionary<string, string>();
        void Visit(JsonElement node, string path)
        {
            if (node.ValueKind == JsonValueKind.Object)
                foreach (var property in node.EnumerateObject()) Visit(property.Value, path + "/" + Escape(property.Name));
            else result[path] = InstallerPaths.Hash(Encoding.UTF8.GetBytes(node.GetRawText()));
        }
        Visit(document.RootElement, "");
        return result;
    }

    public static void Validate(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Configuration must be an object.");
        void Check(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in node.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate configuration key.");
                    Check(property.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Check(item);
        }
        Check(document.RootElement);
    }

    public static byte[] Merge(byte[] existing, byte[] incoming, string format)
    {
        if (format == "toml")
        {
            var original = Encoding.UTF8.GetString(existing);
            var addition = Encoding.UTF8.GetString(incoming);
            if (Toml.Parse(original).HasErrors || Toml.Parse(addition).HasErrors) throw new InvalidDataException("Malformed TOML configuration.");
            var before = Toml.ToModel(original);
            var after = Toml.ToModel(addition);
            // Appending complete, absent top-level tables is safe; overlapping TOML tables require manual integration.
            if (after.Keys.Any(before.ContainsKey)) throw new InvalidDataException("Overlapping TOML tables require review; existing settings were preserved.");
            var result = Encoding.UTF8.GetBytes(original + "\n" + addition);
            if (Toml.Parse(Encoding.UTF8.GetString(result)).HasErrors) throw new InvalidDataException("TOML addition is not safe.");
            return result;
        }
        Validate(existing);
        Validate(incoming);
        using var oldDoc = JsonDocument.Parse(existing, DocumentOptions);
        using var newDoc = JsonDocument.Parse(incoming, DocumentOptions);
        var changes = new Dictionary<string, List<JsonProperty>>(StringComparer.Ordinal);
        void Compare(JsonElement oldNode, JsonElement newNode, string path)
        {
            foreach (var property in newNode.EnumerateObject())
            {
                if (!oldNode.TryGetProperty(property.Name, out var current))
                {
                    if (!changes.TryGetValue(path, out var list)) changes[path] = list = [];
                    list.Add(property);
                }
                else if (Equivalent(current, property.Value)) { }
                else if (current.ValueKind == JsonValueKind.Object && property.Value.ValueKind == JsonValueKind.Object)
                    Compare(current, property.Value, path + "/" + Escape(property.Name));
                else throw new InvalidDataException("A configuration key conflicts with an existing value.");
            }
        }
        Compare(oldDoc.RootElement, newDoc.RootElement, "");
        if (changes.Count == 0) return existing;
        var offsets = new Dictionary<string, (int Offset, bool HasProperties)>();
        var reader = new Utf8JsonReader(existing, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
        ReadValue(ref reader, "", offsets, initial: true);
        var edits = changes.Select(p => (At: offsets[p.Key].Offset,
            Text: (offsets[p.Key].HasProperties ? "," : "") + "\n" + string.Join(",\n", p.Value.Select(v => JsonSerializer.Serialize(v.Name) + ": " + v.Value.GetRawText())))).OrderByDescending(e => e.At);
        var output = existing.ToList();
        foreach (var edit in edits) output.InsertRange(edit.At, Encoding.UTF8.GetBytes(edit.Text));
        var merged = output.ToArray();
        Validate(merged);
        return merged;
    }

    private static void ReadValue(ref Utf8JsonReader reader, string path, Dictionary<string, (int, bool)> offsets, bool initial = false)
    {
        if (initial) reader.Read();
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var insertion = (int)reader.BytesConsumed;
            var any = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString()!;
                reader.Read();
                ReadValue(ref reader, path + "/" + Escape(name), offsets);
                insertion = (int)reader.BytesConsumed;
                any = true;
            }
            offsets[path] = (insertion, any);
        }
        else if (reader.TokenType == JsonTokenType.StartArray) reader.Skip();
    }
    private static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static bool Equivalent(JsonElement left, JsonElement right) => left.ValueKind == right.ValueKind && (left.ValueKind switch
    {
        JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count() && left.EnumerateObject().All(p => right.TryGetProperty(p.Name, out var value) && Equivalent(p.Value, value)),
        JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(p => Equivalent(p.First, p.Second)),
        JsonValueKind.String => left.GetString() == right.GetString(),
        JsonValueKind.Number => left.GetRawText() == right.GetRawText(),
        _ => left.GetRawText() == right.GetRawText()
    });
}
