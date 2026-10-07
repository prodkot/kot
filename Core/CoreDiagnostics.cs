using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kot.Core;

public sealed class CoreLogBuffer(int capacity = 32768)
{
    readonly StringBuilder buffer = new();
    readonly object sync = new();
    public void Append(ReadOnlySpan<char> text)
    {
        lock (sync)
        {
            if (text.Length >= capacity) { buffer.Clear(); buffer.Append(text[^capacity..]); }
            else { int remove = buffer.Length + text.Length - capacity; if (remove > 0) buffer.Remove(0, remove); buffer.Append(text); }
        }
    }
    public string Snapshot() { lock (sync) return buffer.ToString(); }
    public void Clear() { lock (sync) buffer.Clear(); }
}

public static class CoreDiagnostics
{
    public static string Redact(string raw, Node? node, params string[] privateValues)
        => RedactMany(raw, node == null ? [] : new[] { node }, privateValues);
    public static string RedactMany(string raw, IEnumerable<Node> nodes, params string[] privateValues)
    {
        string clean = Regex.Replace(raw, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        clean = new string(clean.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
        clean = Regex.Replace(clean, @"\b[a-z][a-z0-9+.-]*://[^\s<>""']+", "[ссылка скрыта]", RegexOptions.IgnoreCase);
        HashSet<string> values = new(StringComparer.OrdinalIgnoreCase);
        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            values.Add(text);
            values.Add(Uri.EscapeDataString(text));
            values.Add(System.Text.Json.JsonSerializer.Serialize(text)[1..^1]);
        }
        void Walk(JsonNode? value, string key = "")
        {
            if (value is JsonObject obj) foreach (var item in obj) Walk(item.Value, item.Key);
            else if (value is JsonArray arr) foreach (var item in arr) Walk(item, key);
            else if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
                && key is not ("type" or "tag" or "flow" or "method" or "security" or "fingerprint" or "alpn")
                && text is not ("/" or "")) Add(text);
        }
        foreach (var node in nodes) Walk(node.Outbound);
        foreach (string value in privateValues) Add(value);
        foreach (string value in values.OrderByDescending(v => v.Length))
            clean = clean.Replace(value, "[скрыто]", StringComparison.OrdinalIgnoreCase);
        clean = Regex.Replace(clean, @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", "[UUID скрыт]", RegexOptions.IgnoreCase);
        return clean.Trim();
    }
    public static string Tail(string clean, int maxChars = 3000)
    {
        string tail = string.Join('\n', clean.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(12).Select(x => x.Trim()));
        return tail.Length > maxChars ? tail[^maxChars..] : tail;
    }
    public static string Summary(string clean)
    {
        var lines = clean.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
        string line = lines.LastOrDefault(x => x.Contains("FATAL", StringComparison.OrdinalIgnoreCase))
            ?? lines.LastOrDefault(x => x.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            ?? lines.LastOrDefault() ?? "Ядро не записало пояснение.";
        return line.Length > 450 ? line[..450] + "…" : line;
    }
}
