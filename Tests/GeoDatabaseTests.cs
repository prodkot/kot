using Kot.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
namespace Kot.Tests;

static class GeoDatabaseTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "kot-geo-schema-" + Guid.NewGuid().ToString("N"));
        var node = Subscriptions.ParseNode("vless://11111111-1111-4111-8111-111111111111@127.0.0.1:443?security=none") with
        {
            FullConfig = JsonNode.Parse("""{"inbounds":[],"outbounds":[{"protocol":"vless","tag":"proxy","settings":{"vnext":[{"address":"127.0.0.1","port":443,"users":[{"id":"11111111-1111-4111-8111-111111111111","encryption":"none"}]}]}}],"routing":{"rules":[{"type":"field","domain":["geosite:ru"],"outboundTag":"proxy"}]}}""")!.AsObject()
        };
        try
        {
            var cache = new XrayAssets(root);
            foreach (byte[] bad in new byte[][] {
                [10,2,255,255], // valid envelope, invalid nested wire
                [10,4,10,2,255,255], // invalid UTF8 country
                [10,8,10,2,114,117,18,2,255,255], // corrupt nested domain
                [10,8,10,2,114,117,18,2,8,255] // truncated enum varint
            })
            {
                using var transport = new Handler(bad); using var client = new HttpClient(transport);
                bool rejected = false;
                try { await cache.Prepare([node], client, CancellationToken.None); } catch (UserError) { rejected = true; }
                check(rejected && transport.Requests == 1 && !cache.Available(node)
                    && !Directory.GetFiles(root, "*", SearchOption.AllDirectories).Any(), "nested invalid geodata is rejected before acceptance with one bounded download");
            }
            string path = Path.Combine(cache.DirectoryFor(node), "geosite.dat");
            await File.WriteAllBytesAsync(path, [10,2,255,255]);
            await File.WriteAllTextAsync(path + ".sha256", Convert.ToHexStringLower(SHA256.HashData(new byte[] {10,2,255,255})));
            check(!cache.Available(node), "old poisoned cache is invalid even when its checksum matches");
            using var good = new Handler(GeoFixtures.Site); using var goodClient = new HttpClient(good);
            await cache.Prepare([node], goodClient, CancellationToken.None);
            check(good.Requests == 1 && cache.Available(node), "same URL and revision recover after the source fixes its content");
            await cache.Prepare([node], goodClient, CancellationToken.None);
            check(good.Requests == 1, "valid checked geodata works offline without repeated downloads");
            var next = node with { GeoRevision = 1 };
            using var failing = new Handler([10,2,255,255]); using var failingClient = new HttpClient(failing);
            try { await cache.Prepare([next], failingClient, CancellationToken.None); } catch (UserError) { }
            check(cache.Available(node) && File.ReadAllBytes(path).SequenceEqual(GeoFixtures.Site), "rejected new revision preserves the previous working bytes");
            File.Delete(path + ".sha256");
            await cache.Prepare([node], goodClient, CancellationToken.None);
            check(good.Requests == 1 && cache.Available(node), "legacy valid database is adopted without a network request");
            // Native integration uses these exact accepted structures.
            Directory.CreateDirectory("Tests/generated/geodata");
            File.WriteAllBytes("Tests/generated/geodata/geosite.dat", GeoFixtures.Site);
            File.WriteAllBytes("Tests/generated/geodata/geoip.dat", GeoFixtures.Ip);
            File.WriteAllText("Tests/generated/geodata/config.json", XrayRuntime.Build(node, 19442).ToJsonString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    sealed class Handler(byte[] body) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }); }
    }
}
