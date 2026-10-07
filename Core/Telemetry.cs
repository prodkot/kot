using System.Text.Json;
namespace Kot.Core;
public sealed record ConnectionInfo(string Id, string Destination, string Network, string Inbound, string Process, string[] Chains, string Rule, long Download, long Upload, DateTimeOffset? Start);
public sealed record ConnectionSnapshot(long DownloadTotal, long UploadTotal, int Count, ConnectionInfo[] Connections)
{
    public static ConnectionSnapshot Parse(string json)
    {
        if (json.Length > 2 * 1024 * 1024) throw new UserError("Слишком большой ответ статистики.");
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        long Counter(JsonElement obj, string key) => obj.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) && n >= 0 ? n : throw new JsonException("Invalid traffic counter: " + key);
        string Text(JsonElement obj, string key, int limit = 512) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? new string((v.GetString() ?? "").Take(limit).Where(c => !char.IsControl(c)).ToArray()) : "";
        var list = new List<ConnectionInfo>(); int count = 0;
        if (root.TryGetProperty("connections", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            count = array.GetArrayLength();
            foreach (var row in array.EnumerateArray().Take(300))
            {
                if (!row.TryGetProperty("metadata", out var meta)) continue;
                string host = Text(meta, "host"); if (host.Length == 0) host = Text(meta, "destinationIP");
                string port = Text(meta, "destinationPort", 6); if (host.Contains(':')) host = "[" + host + "]";
                string path = Text(meta, "processPath"); int user = path.LastIndexOf(" (", StringComparison.Ordinal); if (user >= 0) path = path[..user];
                path = path.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
                string[] chains = row.TryGetProperty("chains", out var chain) && chain.ValueKind == JsonValueKind.Array ? chain.EnumerateArray().Take(12).Where(v => v.ValueKind == JsonValueKind.String).Select(v => (v.GetString() ?? "")[..Math.Min(512, (v.GetString() ?? "").Length)]).ToArray() : [];
                DateTimeOffset? start = DateTimeOffset.TryParse(Text(row, "start"), out var date) ? date : null;
                list.Add(new(Text(row, "id", 64), host + (port.Length > 0 ? ":" + port : ""), Text(meta, "network", 10), Text(meta, "type", 60), path, chains, Text(row, "rule"), Counter(row, "download"), Counter(row, "upload"), start));
            }
        }
        return new(Counter(root, "downloadTotal"), Counter(root, "uploadTotal"), count, list.ToArray());
    }
}
public sealed record TrafficReading(bool Available, double? DownloadRate, double? UploadRate, long DownloadTotal, long UploadTotal, int Count)
{
    public static TrafficReading Empty { get; } = new(false, null, null, 0, 0, 0);
}
// Time is supplied from Stopwatch, not wall-clock, so sleep/clock corrections cannot produce negative rates.
public sealed class TrafficSampler
{
    int? session;
    double previousTime;
    long download, upload;
    public void Reset() { session = null; previousTime = 0; download = upload = 0; }
    public TrafficReading Sample(int currentSession, double seconds, ConnectionSnapshot value)
    {
        double? down = null, up = null; double elapsed = seconds - previousTime;
        if (session == currentSession && elapsed > 0 && elapsed <= 10 && value.DownloadTotal >= download && value.UploadTotal >= upload)
        { down = (value.DownloadTotal - download) / elapsed; up = (value.UploadTotal - upload) / elapsed; }
        session = currentSession; previousTime = seconds; download = value.DownloadTotal; upload = value.UploadTotal;
        return new(true, down, up, download, upload, value.Count);
    }
}
