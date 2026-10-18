using Kot.Core;
using System.Diagnostics;

namespace Kot.Tests;
public static class GeoIpCoreTests
{
    public static async Task Run(string coreExe, string? downloadedFile)
    {
        coreExe = Path.GetFullPath(coreExe);
        string folder = Path.GetFullPath("Tests/generated/geoip-real"); Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "source.cidr");
        File.WriteAllText(source, "203.0.113.0/24\n2001:db8::/32\n");
        await GeoIp.ValidateFile(source, coreExe, CancellationToken.None);
        if (downloadedFile != null) await GeoIp.ValidateFile(Path.GetFullPath(downloadedFile), coreExe, CancellationToken.None);
        var cache = new GeoIpCache(Path.Combine(folder, "cache-native"));
        await cache.Update(["ru", "us", "cn"], false, (path, ct) => GeoIp.ValidateFile(path, coreExe, ct), CancellationToken.None,
            (_, _) => File.ReadAllBytesAsync(downloadedFile == null ? source : Path.GetFullPath(downloadedFile)));
        var node = Subscriptions.ParseNode("vless://c9a83ccf-d2e9-4ef2-a8b4-b1b8cb5f52a1@example.com:443?security=tls&sni=example.com#fixture");
        var rules = new RoutingRules { DirectCountries = ["ru"], ProxyCountries = ["us"], BlockCountries = ["cn"] };
        foreach (bool tun in new[] { false, true })
        {
            var config = XrayRuntime.Build(node, 18780, tun, tun ? 0 : 18782, manual: rules, geoIpPaths: cache.Paths(rules.Countries()));
            string path = Path.Combine(folder, tun ? "tun.json" : "proxy.json"); File.WriteAllText(path, config.ToJsonString());
            await Command(coreExe, "run", "-test", "-c", path);
        }
        Console.WriteLine("PASS native Xray accepts IPv4/IPv6 country routing in TUN and system proxy configurations");
        foreach (string text in new[] { "SRS\u0001broken-compression", "example.org", "1.2.3.4/99", "<html>download failed</html>" })
        {
            File.WriteAllText(source, text); bool rejected = false;
            try { await GeoIp.ValidateFile(source, coreExe, CancellationToken.None); } catch (UserError) { rejected = true; }
            if (!rejected) throw new Exception("Invalid GeoIP passed validation");
        }
        Console.WriteLine("PASS native GeoIP validator rejects corrupt data, domains, bad prefixes and HTML");
    }
    static async Task Command(string core, params string[] args)
    {
        var psi = new ProcessStartInfo(core) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in args) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi)!;
        Task<string> error = process.StandardError.ReadToEndAsync(), output = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } await Task.WhenAll(error, output); }
        if (process.ExitCode != 0) throw new Exception(error.Result + output.Result);
    }
}
