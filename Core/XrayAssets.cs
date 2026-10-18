using System.Security.Cryptography;
using System.Text;
namespace Kot.Core;

// Each pair of provider URLs and revision has its own cache. A failed download
// never overwrites the files used by the currently saved subscription.
public sealed class XrayAssets(string root)
{
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
    public bool Available(Node node) => node.FullConfig == null || XrayConfig.GeoFiles(node.FullConfig).All(f => File.Exists(Path.Combine(DirectoryFor(node), f)));
    public async Task Prepare(IEnumerable<Node> nodes, HttpClient client, CancellationToken ct)
    {
        foreach (var node in nodes.Where(n => n.FullConfig != null).DistinctBy(n => DirectoryFor(n) + string.Join(',', XrayConfig.GeoFiles(n.FullConfig!))))
        {
            foreach (string file in XrayConfig.GeoFiles(node.FullConfig!))
            {
                string directory = DirectoryFor(node), path = Path.Combine(directory, file);
                if (File.Exists(path)) continue;
                Directory.CreateDirectory(directory);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var uri = RoutingProfiles.Source(Source(node, file));
                    for (int redirects = 0; ; redirects++)
                    {
                        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                        if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                        {
                            if (redirects >= 5 || response.Headers.Location == null) throw new UserError("Слишком много перенаправлений геобазы Xray.");
                            uri = RoutingProfiles.Source(new Uri(uri, response.Headers.Location).AbsoluteUri); continue;
                        }
                        if (!response.IsSuccessStatusCode) throw new UserError($"Геобаза Xray вернула HTTP {(int)response.StatusCode}.");
                        if (response.Content.Headers.ContentLength > MaxBytes) throw new UserError("Геобаза Xray больше 64 МиБ.");
                        await using var input = await response.Content.ReadAsStreamAsync(ct);
                        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        {
                            byte[] buffer = new byte[65536]; int count, total = 0;
                            while ((count = await input.ReadAsync(buffer, ct)) > 0)
                            {
                                total += count; if (total > MaxBytes) throw new UserError("Геобаза Xray больше 64 МиБ.");
                                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                            }
                            if (total < 4) throw new UserError("Пустая геобаза Xray.");
                        }
                        // Both official protobuf lists start with field 1, length-delimited.
                        using (var check = File.OpenRead(temporary)) if (check.ReadByte() != 0x0a) throw new UserError("Вместо геобазы Xray получен другой файл.");
                        File.Move(temporary, path, overwrite: false); break;
                    }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }
}
