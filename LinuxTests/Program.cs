using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kot.Core;
using Kot.Linux;

const string servicesTestPassword = "kot-services-backup-test";
if (args.FirstOrDefault() == "--services-backup-fixture")
{
    var fixture = new Profile();
    fixture.Services.CheckUpdates = true;
    fixture.Services.Telegram.AutoStart = true;
    fixture.Services.Zapret.AutoStart = true;
    File.WriteAllBytes(args[1], Backup.Export(fixture, servicesTestPassword));
    return;
}
if (args.FirstOrDefault() == "--verify-services-backup")
{
    var fixture = Backup.Import(File.ReadAllBytes(args[1]), servicesTestPassword);
    if (!fixture.Services.CheckUpdates || !fixture.Services.Telegram.AutoStart || !fixture.Services.Zapret.AutoStart)
        throw new Exception("Saved service settings were modified");
    Console.WriteLine("PASS dormant service settings preserved in portable backup");
    return;
}

string root = Environment.GetEnvironmentVariable("KOT_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "kot-linux-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
foreach (string name in new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_RUNTIME_DIR" }) Environment.SetEnvironmentVariable(name, Path.Combine(root, name));
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
using var serverReservation = LoopbackPort.Reserve();
int serverPort = serverReservation.Port;
var node = Subscriptions.ParseNode($"vless://11111111-1111-4111-8111-111111111111@127.0.0.1:{serverPort}?encryption=none#Linux");
var linux = XrayRuntime.Build(node, 19492, true, platform: XrayPlatform.Linux);
var settings = linux["inbounds"]!.AsArray().Single(n => n!["protocol"]!.ToString() == "tun")!["settings"]!.AsObject();
Check(settings.ToJsonString() == "{\"name\":\"kot-tun\",\"mtu\":1500}", "Linux TUN has no Wintun/WFP/automatic OS mutations");
var windows = XrayRuntime.Build(node, 19492, true);
Check(windows["inbounds"]![1]!["settings"]!["autoSystemWfpBlockLeak"] != null, "Windows TUN defaults retained");
try { XrayRuntime.Build(node, 19492, manual: new RoutingRules { DirectApps = ["chrome.exe"] }, platform: XrayPlatform.Linux); throw new Exception("accepted Windows app rule"); } catch (UserError) { Console.WriteLine("PASS Windows executable routing rejected on Linux"); }
var profile = new Profile(); var sub = new SavedSubscription { Name = "Linux test", Local = true, Nodes = [node], Selected = node.Id }; profile.Subscriptions.Add(sub); profile.Activate(sub.Id);
using (var store = new Store(RandomNumberGenerator.GetBytes(32)))
{
    store.Save(profile);
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Paths.Data, "profile.bin"))).Contains("11111111"), "Linux profile encrypted at rest");
    Check(store.Load().Nodes.Single().Id == node.Id, "encrypted profile roundtrip");
    var changed = store.Change(profile, p => p.Theme = "light");
    Check(store.Load().Theme == "light" && File.Exists(Path.Combine(Paths.Data, "profile.previous.bin")), "atomic replacement retains previous profile");
    using var wrong = new Store(RandomNumberGenerator.GetBytes(32));
    try { wrong.Load(); throw new Exception("wrong key accepted"); } catch (CryptographicException) { Console.WriteLine("PASS wrong Secret Service key fails closed"); }
}
var fake = new Dictionary<string, string>();
var original = new Dictionary<string, string>();
var proxy = new GnomeProxy(args =>
{
    string key = args[1] + "/" + args[2];
    if (args[0] == "set") fake[key] = args[3];
    if (!fake.ContainsKey(key)) { fake[key] = key.EndsWith("/mode") ? "'auto'" : key.EndsWith("/port") ? "9000" : key.EndsWith("/use-authentication") || key.EndsWith("/use-same-proxy") ? "true" : key.EndsWith("/ignore-hosts") ? "['original.test']" : "'original.test'"; original[key] = fake[key]; }
    return Task.FromResult(fake[key]);
});
await proxy.Enable(19493);
fake["org.gnome.system.proxy.https/host"] = "'user-change.test'";
await proxy.Restore();
Check(fake["org.gnome.system.proxy/mode"] == "'auto'" && fake["org.gnome.system.proxy.http/port"] == "9000", "GNOME compare-and-restore returns owned settings");
Check(fake["org.gnome.system.proxy.https/host"] == "'user-change.test'", "GNOME restore preserves intervening user change");
Paths.Atomic(Path.Combine(Paths.Config, "guard-active"), "retained protection"u8.ToArray());
var retained = new Tunnel(proxy, new XrayAssets(Path.Combine(Paths.Cache, "guard-assets")), new GeoIpCache(Path.Combine(Paths.Cache, "guard-geo")));
await retained.Connect(profile);
Check(retained.State == "idle" && retained.Guard && File.Exists(Path.Combine(Paths.Config, "guard-active")) && retained.Error.Contains("аварийная блокировка"), "retained kill switch cannot be silently cleared by a connection without protection");
File.Delete(Path.Combine(Paths.Config, "guard-active"));
if (args.Contains("--managed-only")) return;
using (var timeout = new CancellationTokenSource(250))
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    try { await OwnedProcess.Run("/bin/sleep", ["30"], ct: timeout.Token); throw new Exception("timeout ignored"); } catch (OperationCanceledException) { }
    Check(watch.Elapsed < TimeSpan.FromSeconds(8), "owned subprocess cancellation bounded");
}
await using (var child = new OwnedProcess("/usr/bin/python3", ["-c", "import sys; sys.stdout.write('o'*100000); sys.stderr.write('e'*100000)"]))
{
    child.CloseInput(); await child.Wait(CancellationToken.None);
    Check(child.ExitCode == 0 && child.Log.Snapshot().Length < 100000, "stdout/stderr drained with bounded memory");
}
string coreSource = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("artifacts/core/xray");
Directory.CreateDirectory(Path.GetDirectoryName(Paths.Core)!);
string coreTemporary = Paths.Core + ".test-" + Guid.NewGuid().ToString("N");
File.Copy(coreSource, coreTemporary, true); File.SetUnixFileMode(coreTemporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); File.Move(coreTemporary, Paths.Core, true);
using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start(); int originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
using var life = new CancellationTokenSource();
var originTask = Task.Run(async () =>
{
    try
    {
        while (!life.IsCancellationRequested)
        {
            using var socket = await origin.AcceptTcpClientAsync(life.Token);
            using var stream = socket.GetStream(); byte[] buffer = new byte[4096]; int received = await stream.ReadAsync(buffer, life.Token);
            if (received == 0) continue;
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\nreal-vpn"u8.ToArray(), life.Token);
        }
    }
    catch (OperationCanceledException) { }
});
var serverConfig = new JsonObject
{
    ["inbounds"] = new JsonArray(new JsonObject { ["listen"] = "127.0.0.1", ["port"] = serverPort, ["protocol"] = "vless", ["settings"] = new JsonObject { ["decryption"] = "none", ["clients"] = new JsonArray(new JsonObject { ["id"] = "11111111-1111-4111-8111-111111111111" }) } }),
    ["outbounds"] = new JsonArray(new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["finalRules"] = new JsonArray(new JsonObject { ["action"] = "allow", ["ip"] = new JsonArray("127.0.0.0/8", "::1/128") }) } }), ["log"] = new JsonObject { ["loglevel"] = "warning" }
};
serverReservation.Dispose();
await using var server = new OwnedProcess(Paths.Core, ["run", "-c", "stdin:", "-format", "json"]);
await server.Write(serverConfig.ToJsonString(), life.Token); server.CloseInput();
await Task.Delay(300);
var tunnel = new Tunnel(proxy, new XrayAssets(Path.Combine(Paths.Cache, "assets")), new GeoIpCache(Path.Combine(Paths.Cache, "geo")));
var tracked = new UsageTracker(new UsageArchive());
tunnel.Changed = () => { if (tunnel.State == "connected") tracked.Start(tunnel.Session, tunnel.Started, tunnel.StartedUtc); };
tunnel.Ended = (session, final) => tracked.End(session, Tunnel.Clock, DateTimeOffset.UtcNow, final);
profile.ConnectionMode = "proxy";
await tunnel.Connect(profile, $"http://127.0.0.1:{originPort}/");
Check(tunnel.State == "connected", "real Linux tunnel health traverses local VLESS server: " + tunnel.Error + " origin=" + originTask.Status + " server=" + CoreDiagnostics.CreateRedactor([node])(server.Log.Snapshot()));
using (var client = new HttpClient(NetworkRequests.Handler(tunnel.Port))) Check(await client.GetStringAsync($"http://127.0.0.1:{originPort}/") == "real-vpn", "real response through Linux client's HTTP proxy");
var counters = await tunnel.Metrics(life.Token);
Check(counters != null && counters.DownloadTotal > 0, "authenticated Xray metrics count actual traffic");
var quality = await ConnectionCheck.Run(tunnel.HealthPort, new PingOptions { Url = $"http://127.0.0.1:{originPort}/", Attempts = 2, TimeoutMs = 1000 }, () => tunnel.State == "connected", _ => { }, life.Token);
Check(quality.Successes == 2, "connection quality samples traverse forced-proxy health inbound");
await tunnel.Stop();
Check(tunnel.State == "idle" && fake["org.gnome.system.proxy/mode"] == "'auto'", "disconnect stops owned core and restores proxy");
Check(tracked.ActiveSession == null && tracked.Archive.AllTime.Sessions == 1, "manual disconnect closes statistics generation");
await tunnel.Connect(profile, $"http://127.0.0.1:{originPort}/");
Check(tunnel.State == "connected" && tracked.Archive.AllTime.Sessions == 2, "reconnect starts a separate statistics generation");
await tunnel.Stop();
await server.Stop();
var connecting = tunnel.Connect(profile, $"http://127.0.0.1:{originPort}/");
await Task.Delay(500); tunnel.Cancel(); await connecting;
Check(tunnel.State == "idle" && tunnel.Port == 0, "cancel failed connection cleans up without pretending success");
life.Cancel(); await originTask;
Console.WriteLine("Evidence directory: " + root);
if (args.Contains("--gnome"))
{
    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KOT_ISOLATED_DBUS"))) throw new Exception("Real GSettings test requires isolated D-Bus/XDG; refusing host settings.");
    var actual = new GnomeProxy();
    string mode = await OwnedProcess.Run("/usr/bin/gsettings", ["get", "org.gnome.system.proxy", "mode"]);
    await actual.Enable(19493);
    Check(await OwnedProcess.Run("/usr/bin/gsettings", ["get", "org.gnome.system.proxy", "mode"]) == "'manual'", "real isolated GNOME GSettings proxy applied");
    await actual.Restore();
    Check(await OwnedProcess.Run("/usr/bin/gsettings", ["get", "org.gnome.system.proxy", "mode"]) == mode, "real isolated GNOME GSettings proxy restored");
}
if (args.Contains("--telegram"))
{
    int cached = Array.IndexOf(args, "--telegram-cache");
    if (cached >= 0)
    {
        string source = Path.GetFullPath(args[cached + 1]);
        var descriptor = System.Text.Json.JsonSerializer.Deserialize<ServiceRelease>(File.ReadAllBytes(Path.Combine(source, "current.json")))!;
        Check(descriptor.Id == "telegram" && descriptor.Platform == ServicePlatform.Linux && ServicePackages.ValidVersion(descriptor.Version), "cached package is a Linux Telegram descriptor");
        string target = Paths.Private(Path.Combine(Paths.Data, "services/telegram"));
        Directory.CreateDirectory(Path.Combine(target, descriptor.Version));
        File.Copy(Path.Combine(source, "current.json"), Path.Combine(target, "current.json"));
        string binary = Path.Combine(target, descriptor.Version, "TgWsProxy_linux_amd64");
        File.Copy(Path.Combine(source, descriptor.Version, "TgWsProxy_linux_amd64"), binary);
        File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    var manager = new TelegramService(() => profile.Services.Telegram, () => 0, () => true);
    if (cached < 0) { manager.Begin("install"); await manager.Work; Check(manager.Error.Length == 0, "official Linux Telegram package download and SHA-256: " + manager.Error); }
    else Console.WriteLine("PASS previously downloaded official Linux Telegram cache revalidated by exact size and SHA-256 (offline lifecycle test)");
    manager.Begin("start"); await manager.Work;
    Check(manager.Running, "official Linux Telegram opens loopback port: " + manager.Error);
    Check(!System.Text.Json.JsonSerializer.Serialize(manager.Model()).Contains(profile.Services.Telegram.Secret, StringComparison.OrdinalIgnoreCase), "upstream Telegram log is redacted before UI/journal");
    manager.Begin("stop"); await manager.Work;
    Check(!manager.Running && Directory.GetFiles(Path.Combine(Paths.Data, "services/telegram"), "config.json", SearchOption.AllDirectories).Length == 0, "Telegram stops owned process tree and removes plaintext runtime config");
    try { await Latency.Tcp("127.0.0.1", profile.Services.Telegram.Port, 500, CancellationToken.None); throw new Exception("Telegram port is still open after stop"); }
    catch (SocketException) { Console.WriteLine("PASS official Telegram loopback listener actually closes after stop"); }
    Check(Directory.GetFiles(Path.Combine(Paths.Data, "services/telegram"), "*.log", SearchOption.AllDirectories).Length == 0, "upstream Telegram log FIFO leaves no secret-bearing disk log");
    await manager.Shutdown();
}
