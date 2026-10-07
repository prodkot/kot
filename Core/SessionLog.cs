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
    public string Tail(int limit = 65536) { lock (sync) { string text = buffer.ToString(); return text.Length > limit ? "[Показан конец журнала. Полный журнал доступен по кнопке копирования.]\n" + text[^limit..] : text; } }
    public string Snapshot() { lock (sync) return (truncated ? "[Начало журнала удалено: достигнут лимит 1 МБ символов.]\n" : "") + buffer; }
}
