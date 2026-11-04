using System.Security.Cryptography;
using System.Text;
namespace Kot.Core;

// Each pair of provider URLs and revision has its own cache. A failed download
// never overwrites the files used by the currently saved subscription.
public sealed class XrayAssets(string root)
{
    readonly SemaphoreSlim prepareGate = new(1, 1);
    public const int MaxBytes = 64 * 1024 * 1024;
    public string DirectoryFor(Node node)
    {
        string source = Source(node, "geoip.dat") + "\n" + Source(node, "geosite.dat");
        // Keep the existing cache path for profiles without a version marker.
        if (node.GeoRevision != 0) source += "\n" + node.GeoRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return Path.Combine(root, key);
    }
    static string Source(Node node, string file) => (file == "geoip.dat" ? node.GeoIpUrl : node.GeoSiteUrl) is { Length: > 0 } custom ? custom
        : "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/" + file;
    static bool ValidFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is < 4 or > MaxBytes) return false;
            string receipt = path + ".sha256";
            // Older clients have no checksum receipt. Keep their offline caches
            // usable, but reject a truncated protobuf envelope before adoption.
            if (!File.Exists(receipt)) return LegacyEnvelope(path);
            if (new FileInfo(receipt).Length != 64) return false;
            string expected = File.ReadAllText(receipt);
            using var input = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(input)) == expected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    static bool LegacyEnvelope(string path)
    {
        using var input = File.OpenRead(path);
        int records = 0;
        while (input.Position < input.Length)
        {
            if (input.ReadByte() != 0x0a) return false;
            uint length = 0; bool ended = false;
            for (int shift = 0; shift < 35; shift += 7)
            {
                int part = input.ReadByte(); if (part < 0 || shift == 28 && part > 15) return false;
                length |= (uint)(part & 127) << shift;
                if ((part & 128) == 0) { ended = true; break; }
            }
            if (!ended || length == 0 || length > input.Length - input.Position) return false;
            input.Position += length; records++;
        }
        return records > 0;
    }
    public bool Available(Node node) => node.FullConfig == null || XrayConfig.GeoFiles(node.FullConfig).All(f => ValidFile(Path.Combine(DirectoryFor(node), f)));
    public async Task Prepare(IEnumerable<Node> nodes, HttpClient client, CancellationToken ct)
    {
        // Import, reconnect and background checks share this cache instance.
        await prepareGate.WaitAsync(ct);
        try { await PrepareLocked(nodes, client, ct); }
        finally { prepareGate.Release(); }
    }
    async Task PrepareLocked(IEnumerable<Node> nodes, HttpClient client, CancellationToken ct)
    {
        foreach (var node in nodes.Where(n => n.FullConfig != null).DistinctBy(n => DirectoryFor(n) + string.Join(',', XrayConfig.GeoFiles(n.FullConfig!))))
        {
            foreach (string file in XrayConfig.GeoFiles(node.FullConfig!))
            {
                string directory = DirectoryFor(node), path = Path.Combine(directory, file);
                ct.ThrowIfCancellationRequested();
                if (ValidFile(path))
                {
                    if (!File.Exists(path + ".sha256"))
                    {
                        string adoption = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            using var legacy = File.OpenRead(path);
                            await File.WriteAllTextAsync(adoption, Convert.ToHexStringLower(SHA256.HashData(legacy)), ct);
                            ct.ThrowIfCancellationRequested();
                            File.Move(adoption, path + ".sha256", true);
                        }
                        finally { if (File.Exists(adoption)) File.Delete(adoption); }
                    }
                    continue;
                }
                Directory.CreateDirectory(directory);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                string receiptTemp = temporary + ".sha256";
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(client.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(45)
                        : TimeSpan.FromMilliseconds(Math.Min(45000, client.Timeout.TotalMilliseconds)));
                    var token = deadline.Token;
                    var uri = RoutingProfiles.Source(Source(node, file));
                    for (int redirects = 0; ; redirects++)
                    {
                        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                        if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                        {
                            if (redirects >= 5 || response.Headers.Location == null) throw new UserError("Слишком много перенаправлений геобазы Xray.");
                            uri = RoutingProfiles.Source(new Uri(uri, response.Headers.Location).AbsoluteUri); continue;
                        }
                        if (!response.IsSuccessStatusCode) throw new UserError($"Геобаза Xray вернула HTTP {(int)response.StatusCode}.");
                        if (response.Content.Headers.ContentLength > MaxBytes) throw new UserError("Геобаза Xray больше 64 МиБ.");
                        await using var input = await response.Content.ReadAsStreamAsync(token);
                        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        {
                            byte[] buffer = new byte[65536]; int count, total = 0;
                            while ((count = await input.ReadAsync(buffer, token)) > 0)
                            {
                                total += count; if (total > MaxBytes) throw new UserError("Геобаза Xray больше 64 МиБ.");
                                await output.WriteAsync(buffer.AsMemory(0, count), token);
                            }
                            if (total < 4) throw new UserError("Пустая геобаза Xray.");
                        }
                        // Check every top-level length-delimited protobuf record.
                        // Native Xray remains responsible for the actual schema.
                        if (!LegacyEnvelope(temporary)) throw new UserError("Повреждённая геобаза Xray или получен другой файл.");
                        using (var check = File.OpenRead(temporary))
                            await File.WriteAllTextAsync(receiptTemp, Convert.ToHexStringLower(SHA256.HashData(check)), token);
                        token.ThrowIfCancellationRequested();
                        // A crash between these replacements leaves an invalid pair which
                        // will be repaired on the next preparation.
                        File.Move(temporary, path, overwrite: true);
                        File.Move(receiptTemp, path + ".sha256", overwrite: true); break;
                    }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); if (File.Exists(receiptTemp)) File.Delete(receiptTemp); }
            }
        }
    }
}
