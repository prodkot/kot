using Kot.Core;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
static class SecurityTests
{
    public static async Task Run(Action<bool, string> check, string vless, string uuid)
    {
        string Reject(Action action, string label)
        {
            bool rejected = false; string message = ""; try { action(); } catch (UserError ex) { rejected = true; message = ex.Message; }
            check(rejected, label);
            return message;
        }
        foreach (string flag in new[] { "allowInsecure=1", "insecure=TrUe", "allowInsecure=false&insecure=true" })
            foreach (string protocol in new[] { "vless", "trojan", "hy2" })
                Reject(() => Subscriptions.ParseNode($"{protocol}://{uuid}@example.com:443?security=tls&{flag}"), "insecure TLS URI rejected: " + protocol + " " + flag);
        string vmess = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { add = "example.com", port = 443, id = uuid, tls = "tls", allowInsecure = true })));
        Reject(() => Subscriptions.ParseNode(vmess), "VMess boolean insecure flag rejected");
        var mixed = Subscriptions.Parse(vless + "\ntrojan://password@example.com:443?allowInsecure=1");
        check(mixed.Nodes.Count == 1 && mixed.Warnings.Single().Contains("TLS"), "unsafe node skipped with reason, valid subscription retained");
        var good = Subscriptions.ParseNode(vless); var bad = good with { Outbound = (JsonObject)good.Outbound.DeepClone() };
        bad.Outbound["tls"]!["insecure"] = true;
        Reject(() => Configuration.Build(bad, 12345, false, []), "old saved unsafe node cannot start a tunnel or HTTP ping");
        Reject(() => Configuration.BuildAutomatic([good, bad], 12345, false, [], new()), "Auto validates every node, including nonfirst candidate");
        var profile = new Profile { Address = "https://example.com/sub", Nodes = [bad] };
        Reject(() => profile.Validate(), "restored profile cannot bypass TLS verification policy");
        Reject(() => Backup.Export(profile, "test-password"), "unsafe backup export rejected");

        // A corrupt ZIP may lie about its uncompressed lengths. No live release key is used.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            using (var output = zip.CreateEntry("release.json").Open()) output.Write(new byte[2 * 1024 * 1024]);
            using (var output = zip.CreateEntry("release.sig").Open()) output.Write(new byte[16]);
            using (var output = zip.CreateEntry("Kot.exe").Open()) output.Write(new byte[16]);
        }
        byte[] bytes = buffer.ToArray();
        for (int i = 0; i < bytes.Length - 46; i++)
            if (BitConverter.ToUInt32(bytes, i) == 0x02014b50 && Encoding.UTF8.GetString(bytes, i + 46, BitConverter.ToUInt16(bytes, i + 28)) == "release.json")
                BitConverter.GetBytes(1u).CopyTo(bytes, i + 24);
        using var lyingZip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Reject(() => ReleasePackage.Validate(lyingZip), "ZIP with falsified uncompressed manifest size rejected");

        using (var rsa = System.Security.Cryptography.RSA.Create(2048))
        using (var signedBuffer = new MemoryStream())
        {
            var files = new Dictionary<string, byte[]> { ["Kot.exe"] = new byte[32768], ["core/sing-box.exe"] = new byte[2] };
            var hashes = files.ToDictionary(x => x.Key, x => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(x.Value)));
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest("0.2.2", hashes));
            byte[] signature = rsa.SignData(manifest, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using (var zip = new ZipArchive(signedBuffer, ZipArchiveMode.Create, true))
            {
                void Add(string name, byte[] data) { using var output = zip.CreateEntry(name).Open(); output.Write(data); }
                Add("release.json", manifest); Add("release.sig", signature);
                foreach (var file in files) Add(file.Key, file.Value);
            }
            byte[] signed = signedBuffer.ToArray();
            for (int i = 0; i < signed.Length - 46; i++)
                if (BitConverter.ToUInt32(signed, i) == 0x02014b50 && Encoding.UTF8.GetString(signed, i + 46, BitConverter.ToUInt16(signed, i + 28)) == "Kot.exe")
                    BitConverter.GetBytes(1u).CopyTo(signed, i + 24);
            using var lyingFileZip = new ZipArchive(new MemoryStream(signed), ZipArchiveMode.Read);
            Reject(() => ReleasePackage.Validate(lyingFileZip, publicKey: rsa.ExportSubjectPublicKeyInfoPem()), "signed ZIP with valid hashes but false payload size rejected");
        }

        // Real 35-second application deadline, even if headers arrived immediately.
        using var transport = new StalledBodyTransport();
        var watch = System.Diagnostics.Stopwatch.StartNew(); bool timedOut = false;
        try { await Subscriptions.Download("https://example.invalid/stall", CancellationToken.None, transport); }
        catch (OperationCanceledException) { timedOut = true; }
        check(timedOut && transport.Body.Cancelled && watch.Elapsed >= TimeSpan.FromSeconds(34) && watch.Elapsed < TimeSpan.FromSeconds(45), "subscription whole-request deadline cancels stalled body after headers");
    }
    sealed class StalledBodyTransport : HttpMessageHandler
    {
        public readonly StalledStream Body = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(Body) });
    }
    sealed class StalledStream : MemoryStream
    {
        public bool Cancelled;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }
}
