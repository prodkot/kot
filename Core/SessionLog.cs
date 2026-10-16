using System.Text;
namespace Kot.Core;
// All entries from this launch, including exceptions and native stdout/stderr.
// Bounded memory; export explicitly marks an overwritten beginning.
public sealed class SessionLog(int capacity = 1024 * 1024)
{
    readonly StringBuilder buffer = new();
    readonly object sync = new();
    bool truncated;
    public void Write(string source, string message)
    {
        string entry = $"[{DateTimeOffset.UtcNow:O}] [{source}] {message}\n";
        lock (sync)
        {
            if (entry.Length >= capacity) { buffer.Clear(); buffer.Append(entry[^capacity..]); truncated = true; }
            else { int remove = buffer.Length + entry.Length - capacity; if (remove > 0) { buffer.Remove(0, remove); truncated = true; } buffer.Append(entry); }
        }
    }
    public void Error(string source, Exception error) => Write(source, error.ToString());
    internal void Sanitize(Func<string, string> clean)
    {
        lock (sync)
        {
            string text = clean(buffer.ToString()); buffer.Clear();
            if (text.Length > 0) buffer.Append(text).Append('\n');
            if (buffer.Length > capacity) { buffer.Remove(0, buffer.Length - capacity); truncated = true; }
        }
    }
    public string Tail(int limit = 65536) { lock (sync) { string text = buffer.ToString(); return text.Length > limit ? "[Показан конец журнала. Полный журнал доступен по кнопке копирования.]\n" + text[^limit..] : text; } }
    public string Snapshot() { lock (sync) return (truncated ? "[Начало журнала удалено: достигнут лимит 1 МБ символов.]\n" : "") + buffer; }
}

// Historical entries are sanitized before storage. Only current profile values
// are retained, so refreshing/removing subscriptions cannot grow a secret archive.
public sealed class RedactedSessionLog(int capacity = 1024 * 1024)
{
    readonly SessionLog journal = new(capacity);
    readonly object sync = new();
    Func<string, string> clean = CoreDiagnostics.CreateRedactor([]);
    public void Register(IEnumerable<Node> nodes, params string[] values)
    {
        var next = CoreDiagnostics.CreateRedactor(nodes, values);
        lock (sync)
        {
            journal.Sanitize(text => next(clean(text)));
            clean = next;
        }
    }
    public string Clean(string raw) { lock (sync) return clean(raw); }
    public void Write(string source, string message) { lock (sync) journal.Write(clean(source), clean(message)); }
    public void Error(string source, Exception error) => Write(source, error.ToString());
    public string Tail() { lock (sync) return journal.Tail(); }
    public string Snapshot() { lock (sync) return journal.Snapshot(); }
}
