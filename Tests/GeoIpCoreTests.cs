using Kot.Core;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Kot.Tests;

public static class GeoIpCoreTests
{
    public static async Task Run(string coreExe, string? downloadedFile)
    {
        coreExe = Path.GetFullPath(coreExe);
        string folder = Path.GetFullPath("Tests/generated/geoip-real"); Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "source.json"), binary = Path.Combine(folder, "geoip.srs");
        File.WriteAllText(source, "{\"version\":1,\"rules\":[{\"ip_cidr\":[\"203.0.113.0/24\",\"2001:db8::/32\"]}]}");
        await Command(coreExe, "rule-set", "compile", source, "--output", binary);
        await GeoIp.ValidateFile(binary, coreExe, CancellationToken.None);
        if (downloadedFile != null) await GeoIp.ValidateFile(Path.GetFullPath(downloadedFile), coreExe, CancellationToken.None);
        Console.WriteLine("PASS real sing-box accepts GeoIP IPv4/IPv6 SRS" + (downloadedFile == null ? "" : " and the official downloaded country database"));
        var cache = new GeoIpCache(Path.Combine(folder, "cache"));
        await cache.Update(["ru", "us", "cn"], false, (path, ct) => GeoIp.ValidateFile(path, coreExe, ct), CancellationToken.None,
            (_, _) => File.ReadAllBytesAsync(downloadedFile == null ? binary : Path.GetFullPath(downloadedFile)));
        var node = Subscriptions.ParseNode("vless://c9a83ccf-d2e9-4ef2-a8b4-b1b8cb5f52a1@example.com:443?security=tls&sni=example.com#fixture");
        var routing = new RoutingProfile { Rules = new() { DirectCountries = ["ru"], ProxyCountries = ["us"], BlockCountries = ["cn"] } };
        var profile = new Profile { Nodes = [node], Selected = node.Id, RoutingProfiles = [routing], ActiveRoutingProfile = routing.Id };
        foreach (string mode in new[] { "tun", "proxy" })
        {
            profile.ConnectionMode = mode;
            var config = Configuration.BuildConnection(profile, 18780, mode == "proxy" ? 18782 : 0, geoIpPaths: cache.Paths(routing.Rules.Countries()));
            string path = Path.Combine(folder, mode + ".json"); File.WriteAllText(path, config.ToJsonString());
            await Command(coreExe, "check", "-c", path);
        }
        Console.WriteLine("PASS real sing-box validates country routing in TUN and system proxy configurations");
        string invalid = Path.Combine(folder, "invalid.srs"); File.WriteAllText(invalid, "SRS\u0001broken-compression");
        bool rejected = false;
        try { await GeoIp.ValidateFile(invalid, coreExe, CancellationToken.None); } catch (UserError) { rejected = true; }
        if (!rejected) throw new Exception("Corrupt SRS passed validation");
        File.WriteAllText(source, "{\"version\":1,\"rules\":[{\"domain_suffix\":[\"example.org\"]}]}");
        await Command(coreExe, "rule-set", "compile", source, "--output", invalid);
        rejected = false;
        try { await GeoIp.ValidateFile(invalid, coreExe, CancellationToken.None); } catch (UserError) { rejected = true; }
        if (!rejected) throw new Exception("Non-IP SRS passed GeoIP validation");
        if (Directory.GetFiles(folder, "*.srs.*.json").Length > 0) throw new Exception("Validation left temporary files");
        Console.WriteLine("PASS real sing-box GeoIP validator rejects corrupted and non-IP rule sets and cleans temporary files");
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
