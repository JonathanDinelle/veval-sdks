using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Veval.Sdk.Internal;

/// <summary>
/// Normalizes step inputs/outputs into a canonical string so values from different sources compare
/// equal: a live object from the current run, a JsonElement loaded from the API, or a stored baseline.
/// Object keys are sorted and ignored fields dropped, so key order and volatile fields never cause a diff.
/// </summary>
internal static class SnapshotValues
{
    public static string Canonicalize(object? value, IReadOnlyCollection<string> ignoreFields)
    {
        if (value is null) return "null";
        if (value is string s) return s;

        JsonNode? node;
        try
        {
            node = value is JsonElement el ? JsonNode.Parse(el.GetRawText()) : JsonSerializer.SerializeToNode(value);
        }
        catch (Exception)
        {
            return value.ToString() ?? "";
        }

        // A JSON string (e.g. a JsonElement holding text) compares as its raw text, same as a live string.
        if (node is JsonValue v && v.TryGetValue<string>(out var text)) return text;

        return Write(Normalize(node, ignoreFields));
    }

    /// <summary>
    /// A readable rendering for diffs: sorted keys, one value per line, and multi-line strings (prompts)
    /// expanded line by line — so editing one line of a prompt diffs as one line, not one giant JSON string.
    /// </summary>
    public static string Display(object? value, IReadOnlyCollection<string> ignoreFields)
    {
        if (value is null) return "null";
        if (value is string s) return s;

        JsonNode? node;
        try
        {
            node = value is JsonElement el ? JsonNode.Parse(el.GetRawText()) : JsonSerializer.SerializeToNode(value);
        }
        catch (Exception)
        {
            return value.ToString() ?? "";
        }
        if (node is JsonValue v && v.TryGetValue<string>(out var text)) return text;

        var sb = new StringBuilder();
        Render(Normalize(node, ignoreFields), 0, sb);
        return sb.ToString().TrimEnd();
    }

    private static void Render(JsonNode? node, int indent, StringBuilder sb)
    {
        var pad = new string(' ', indent);
        switch (node)
        {
            case JsonObject obj when obj.Count > 0:
                foreach (var kv in obj) RenderEntry(pad + kv.Key + ":", kv.Value, indent, sb);
                break;
            case JsonArray arr when arr.Count > 0:
                foreach (var item in arr) RenderEntry(pad + "-", item, indent, sb);
                break;
            default:
                sb.Append(pad).AppendLine(Scalar(node));
                break;
        }
    }

    private static void RenderEntry(string label, JsonNode? value, int indent, StringBuilder sb)
    {
        var childPad = new string(' ', indent + 2);
        if (value is JsonObject { Count: > 0 } or JsonArray { Count: > 0 })
        {
            sb.AppendLine(label);
            Render(value, indent + 2, sb);
        }
        else if (value is JsonValue v && v.TryGetValue<string>(out var str) && str.Contains('\n'))
        {
            sb.AppendLine(label + " |");
            foreach (var line in str.Replace("\r\n", "\n").Split('\n'))
                sb.Append(childPad).AppendLine(line);
        }
        else
        {
            sb.Append(label).Append(' ').AppendLine(Scalar(value));
        }
    }

    private static string Scalar(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject => "{}",
        JsonArray => "[]",
        _ => node.ToJsonString(),
    };

    private static JsonNode? Normalize(JsonNode? node, IReadOnlyCollection<string> ignoreFields) => node switch
    {
        JsonObject obj => SortedObject(obj, ignoreFields),
        JsonArray arr => new JsonArray(arr.Select(n => Normalize(n, ignoreFields)).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };

    private static JsonObject SortedObject(JsonObject obj, IReadOnlyCollection<string> ignoreFields)
    {
        var sorted = new JsonObject();
        foreach (var kv in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (ignoreFields.Contains(kv.Key)) continue;
            sorted[kv.Key] = Normalize(kv.Value, ignoreFields);
        }
        return sorted;
    }

    private static string Write(JsonNode? node) =>
        node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";

    /// <summary>
    /// A compact line diff (expected "-", actual "+") around the first differing lines — enough to see what
    /// changed in a prompt without dumping both versions in full.
    /// </summary>
    public static string LineDiff(string expected, string actual, int context = 1, int maxLines = 12)
    {
        var e = expected.Replace("\r\n", "\n").Split('\n');
        var a = actual.Replace("\r\n", "\n").Split('\n');

        var prefix = 0;
        while (prefix < e.Length && prefix < a.Length && e[prefix] == a[prefix]) prefix++;
        var suffix = 0;
        while (suffix < e.Length - prefix && suffix < a.Length - prefix && e[e.Length - 1 - suffix] == a[a.Length - 1 - suffix]) suffix++;

        var sb = new StringBuilder();
        var lines = 0;
        void Add(string line)
        {
            if (lines++ < maxLines) sb.AppendLine(line);
        }

        for (var i = Math.Max(0, prefix - context); i < prefix; i++) Add("  " + e[i]);
        for (var i = prefix; i < e.Length - suffix; i++) Add("- " + e[i]);
        for (var i = prefix; i < a.Length - suffix; i++) Add("+ " + a[i]);
        for (var i = a.Length - suffix; i < Math.Min(a.Length, a.Length - suffix + context); i++) Add("  " + a[i]);
        if (lines > maxLines) sb.AppendLine($"  … {lines - maxLines} more line(s)");

        return sb.ToString().TrimEnd();
    }
}
