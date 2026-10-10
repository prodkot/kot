using Kot.Core;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
string uuid = "c9a83ccf-d2e9-4ef2-a8b4-b1b8cb5f52a1";
string vless = $"vless://{uuid}@example.com:443?security=reality&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sid=0123456789abcdef&sni=example.com&flow=xtls-rprx-vision&type=tcp#%D0%9C%D0%BE%D0%B9%20%D1%81%D0%B5%D1%80%D0%B2%D0%B5%D1%80";
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
await Kot.Windows.MainWindow.CheckConsent(Check);
var a = Subscriptions.Parse(vless + "\r\n" + vless + "\nvless://broken@example.com:443\n");
Check(a.Nodes.Count == 1 && a.Warnings.Count == 1 && a.Nodes[0].Name == "Мой сервер", "unicode, CRLF, deduplication, bad-node report");
Check(a.Nodes[0].Outbound["tls"]?["reality"]?["short_id"]?.ToString() == "0123456789abcdef", "Reality fields");
Check(Subscriptions.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(vless)).TrimEnd('=')).Nodes.Count == 1, "unpadded UTF8 base64 subscription");
var ws = Subscriptions.ParseNode($"vless://{uuid}@[::1]:443?security=tls&type=ws&host=cdn.example.com&path=%2Fws%3Fed%3D2048#WS");
Check(ws.Outbound["server"]?.ToString() == "::1" && ws.Outbound["transport"]?["max_early_data"]?.GetValue<int>() == 2048, "IPv6 and WS early data");
Check(ws.Outbound["tls"]?["insecure"] == null, "TLS verification enabled by default");
var ss = Subscriptions.ParseNode("ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-128-gcm:p@ss:word")) + "@example.com:8388#SS");
Check(ss.Outbound["password"]?.ToString() == "p@ss:word", "SIP002 password preserved");
var vm = Subscriptions.ParseNode("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = "2", ps = "VM", add = "example.com", port = 443, id = uuid, aid = 0, scy = "auto", net = "ws", path = "/ws", host = "example.com", tls = "tls" }))));
Check(vm.Outbound["transport"]?["type"]?.ToString() == "ws", "VMess JSON");
foreach (string invalid in new[] { $"vless://{uuid}@example.com:443?type=xhttp", "ss://YWVzLTEyOC1nY206cHc=@example.com:8388?plugin=v2ray-plugin", "<html>login</html>" })
{ bool rejected = false; try { Subscriptions.Parse(invalid); } catch(UserError) { rejected = true; } Check(rejected, "unsupported format rejected"); }
var domains = Configuration.Domains("example.com\n.example.org\nexample.com");
Check(domains.SequenceEqual(new[] { "example.com", "example.org" }), "rules normalization");
bool badDomain = false; try { Configuration.Domains("https://example.com/path"); } catch(UserError) { badDomain = true; } Check(badDomain, "rules reject URL");
string fixtureFolder = "Tests/generated";
Directory.CreateDirectory(fixtureFolder);
foreach (var n in new[] { a.Nodes[0], ws, ss, vm, Subscriptions.ParseNode("trojan://password@example.com:443?sni=example.com#TR"), Subscriptions.ParseNode("hysteria2://password@example.com:443?sni=example.com#HY") })
 File.WriteAllText($"{fixtureFolder}/{n.Protocol}-{n.Id[..6]}.json", Configuration.Build(n, 19223, true, domains).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
if (args.Length > 0 && args[0] == "integration")
{
 var fixture = Subscriptions.ParseNode($"vless://{uuid}@127.0.0.1:19441?security=none#Fixture");
 File.WriteAllText(Path.Combine(fixtureFolder, "fixture-client.json"), Configuration.Build(fixture, 19442, false, [], tun: false).ToJsonString());
 File.WriteAllText(Path.Combine(fixtureFolder, "fixture-server.json"), new JsonObject { ["inbounds"] = new JsonArray(new JsonObject { ["type"] = "vless", ["listen"] = "127.0.0.1", ["listen_port"] = 19441, ["users"] = new JsonArray(new JsonObject { ["uuid"] = uuid }) }), ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct" }) }.ToJsonString());
}

var redirect = new FakeTransport((request, index) => index == 0 ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("/download", UriKind.Relative) } } : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(vless) });
var fetched = await Subscriptions.Download("https://subscription.example/start", CancellationToken.None, redirect);
Check(fetched.Nodes.Count == 1 && redirect.Addresses.SequenceEqual(new[] { "https://subscription.example/start", "https://subscription.example/download" }), "HTTPS fetch and relative redirect");
bool downgrade = false;
try { await Subscriptions.Download("https://subscription.example/start", CancellationToken.None, new FakeTransport((_, _) => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("http://example.com/plain") } })); } catch(UserError) { downgrade = true; }
Check(downgrade, "HTTPS redirect downgrade rejected");
bool oversized = false;
try { await Subscriptions.Download("https://subscription.example/start", CancellationToken.None, new FakeTransport((_, _) => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(new string('A', Subscriptions.MaxBytes + 1)) })); } catch(UserError) { oversized = true; }
Check(oversized, "oversized HTTPS response rejected");
bool httpRejected = false;
try { await Subscriptions.Download("http://subscription.example/start", CancellationToken.None); } catch(UserError) { httpRejected = true; }
Check(httpRejected, "plain HTTP subscription rejected before fetch");
var device = new SubscriptionDevice(SubscriptionDevice.HashMachineId("ABCDE-TEST-MACHINE"), "Windows", "10.0.26100", ClientIdentity.DeviceName);
device.Validate();
Check(device.Hwid.Length == 64 && device.Hwid == SubscriptionDevice.HashMachineId(" abcde-test-machine ") && device.Hwid != SubscriptionDevice.HashMachineId("other-machine"), "stable app-specific machine ID, normalized and distinct per machine");
Check(device.Hwid == "225CDDEE6C5E4C6F8DCF838D42070CE4DF86F130091DF0AF435B20CFE29E347D", "branding preserves historical HWID algorithm and salt");
Check(ClientIdentity.DeviceName == "kot. windows (0.5.9)", "versioned device display name");
int deviceRequests = 0;
await Subscriptions.Download("https://subscription.example/start", CancellationToken.None, new FakeTransport((request, index) =>
{
    Check(request.Headers.GetValues("x-hwid").Single() == device.Hwid && request.Headers.GetValues("x-device-os").Single() == "Windows"
        && request.Headers.GetValues("x-ver-os").Single() == device.OsVersion && request.Headers.GetValues("x-device-model").Single() == "kot. windows (0.5.9)", "device headers on import and same-origin redirect");
    var userAgent = request.Headers.UserAgent.ToString();
    Check(userAgent == "kot. windows (0.5.9) v2rayN/7.0" && !userAgent.Contains(device.Hwid), "branded User-Agent preserves link-format compatibility without HWID");
    deviceRequests++;
    return index == 0 ? new(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("/download", UriKind.Relative) } } : new(System.Net.HttpStatusCode.OK) { Content = new StringContent(vless) };
}), device);
Check(deviceRequests == 2, "HWID compatible fetch succeeds");
await Subscriptions.Download("https://subscription.example/start", CancellationToken.None, new FakeTransport((request, _) =>
{
    Check(!request.Headers.Contains("x-hwid") && !request.Headers.Contains("x-device-os"), "disabled HWID sends no device headers");
    return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(vless) };
}));
await Subscriptions.Download("https://subscription.example/start", CancellationToken.None, new FakeTransport((request, index) =>
{
    Check(request.Headers.Contains("x-hwid") == (index == 0), "cross-origin redirect does not forward identity or restore it on bounce-back");
    return index switch
    {
        0 => new(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("https://other.example/sub") } },
        1 => new(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("https://subscription.example/end") } },
        _ => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(vless) }
    };
}), device);
await Subscriptions.Download("https://subscription.example/start", CancellationToken.None, new FakeTransport((request, index) =>
{
    Check(request.Headers.Contains("x-hwid") == (index == 0), "different HTTPS port is a different origin");
    return index == 0 ? new(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("https://subscription.example:8443/sub") } } : new(System.Net.HttpStatusCode.OK) { Content = new StringContent(vless) };
}), device);
foreach (string flag in new[] { "x-hwid-max-devices-reached", "x-hwid-limit", "x-hwid-not-supported" })
foreach (var status in new[] { System.Net.HttpStatusCode.OK, System.Net.HttpStatusCode.NotFound })
{
    string message = "";
    try { await Subscriptions.Download("https://subscription.example/start", CancellationToken.None, new FakeTransport((_, _) =>
    { var response = new HttpResponseMessage(status) { Content = new StringContent("") }; response.Headers.Add(flag, "TrUe"); return response; }), device); }
    catch (UserError ex) { message = ex.Message; }
    Check(message.Contains(flag == "x-hwid-not-supported" ? "не принял HWID" : "лимит устройств"), "HWID response flag before HTTP status/body: " + flag + " " + (int)status);
}
string disabledMessage = "";
try { await Subscriptions.Download("https://subscription.example/sub", CancellationToken.None, new FakeTransport((_, _) =>
{ var response = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound); response.Headers.Add("x-hwid-not-supported", "true"); return response; })); }
catch (UserError ex) { disabledMessage = ex.Message; }
Check(disabledMessage.Contains("Включите"), "missing HWID explains how to enable it");
string redirectMessage = "";
try { await Subscriptions.Download("https://subscription.example/sub", CancellationToken.None, new FakeTransport((_, index) =>
{ if (index == 0) return new(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("https://other.example/sub") } }; var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK); response.Headers.Add("x-hwid-not-supported", "true"); return response; }), device); }
catch (UserError ex) { redirectMessage = ex.Message; }
Check(redirectMessage.Contains("прямую ссылку"), "HWID required after cross-origin redirect explains direct-link remedy");
foreach (string badId in new[] { "tiny", new string('a', 65), "aaaaaaaaaa\r\nx-hwid: bad", "abc_defghijkl", "абвгдежзикл" })
{
    bool invalidId = false;
    try { await Subscriptions.Download("https://subscription.example/sub", CancellationToken.None, device: device with { Hwid = badId }); } catch (UserError) { invalidId = true; }
    Check(invalidId, "invalid HWID rejected before any network request");
}
bool badMetadata = false;
try { await Subscriptions.Download("https://subscription.example/sub", CancellationToken.None, device: device with { Model = "PC\r\nInjected: value" }); } catch (UserError) { badMetadata = true; }
Check(badMetadata, "device metadata header injection rejected");
var logs = new CoreLogBuffer(12);
logs.Append("first "); logs.Append("fatal second");
Check(logs.Snapshot() == "fatal second", "stream chunks retain bounded recent output");
logs.Append("0123456789abcdefghijkl");
Check(logs.Snapshot() == "abcdefghijkl", "oversized log chunk keeps bounded tail");
logs.Clear();Check(logs.Snapshot() == "", "log buffer clears on disposal");
var privateNode = Subscriptions.ParseNode("trojan://a%40b%3Asecret@example.com:443?type=ws&path=%2Fprivate-token&sni=cdn.example.com#TR");
string cleanLog = CoreDiagnostics.Redact("\u001b[31mFATAL\u001b[0m create adapter: access denied; credentials a@b:secret /private-token example.com cdn.example.com; https://private.invalid/sub?token=secret; C:\\Users\\Alice\\AppData; " + uuid, privateNode, @"C:\Users\Alice");
Check(!cleanLog.Contains("secret") && !cleanLog.Contains("example.com") && !cleanLog.Contains("Alice") && !cleanLog.Contains(uuid) && !cleanLog.Contains('\u001b') && cleanLog.Contains("create adapter: access denied"), "diagnostics hide credentials, hosts, URL, UUID and local identity while preserving failure");
Check(CoreDiagnostics.Summary("WARN old\nFATAL open tun: failed\nWARN cleanup") == "FATAL open tun: failed", "fatal error takes priority over cleanup messages");
Check(CoreDiagnostics.Summary("WARN old\nERROR DNS unavailable\nWARN cleanup") == "ERROR DNS unavailable", "error summary priority");
Check(CoreDiagnostics.Summary("").Contains("не записало") && CoreDiagnostics.Summary(new string('x', 1000)).Length == 451, "empty output and long diagnostic summary handled");
Check(CoreDiagnostics.Tail(string.Join('\n', Enumerable.Range(1, 40).Select(i => "line" + i))).Split('\n').Length == 12 && CoreDiagnostics.Tail(new string('x', 5000)).Length == 3000, "diagnostic copy length and line count bounded");

// Real loopback checks: no Internet or live subscription is needed.
var pingOptions = new PingOptions(); pingOptions.Validate();
foreach (var invalid in new PingOptions[] { new() { Mode = "icmp" }, new() { Url = "ftp://example.invalid" }, new() { Url = "https://user:secret@example.invalid" }, new() { TimeoutMs = 0 }, new() { Attempts = 6 }, new() { Parallelism = 99 } })
{
    bool rejected = false; try { invalid.Validate(); } catch (UserError) { rejected = true; }
    Check(rejected, "invalid ping setting rejected");
}
using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
{
    listener.Start(); int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    int ms = await Latency.Tcp("127.0.0.1", port, 1000, CancellationToken.None);
    using var accepted = await listener.AcceptTcpClientAsync();
    Check(ms > 0 && ms < 1000, "TCP latency opens a real socket to the node port");
    listener.Stop();
    var refused = await Latency.Sample(new PingOptions { Mode = "tcp", Attempts = 2 }, ct => Latency.Tcp("127.0.0.1", port, 500, ct), CancellationToken.None);
    Check(refused.Status == "error" && refused.Ms == null && refused.Successes == 0 && refused.Attempts == 2, "closed TCP port is failure, never fake latency");
}
using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
{
    listener.Start(); int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var requests = new List<string>();
    var proxy = Task.Run(async () =>
    {
        foreach (int status in new[] { 204, 302 })
        {
            using var client = await listener.AcceptTcpClientAsync(); using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
            requests.Add(await reader.ReadLineAsync() ?? "");
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            string headers = $"HTTP/1.1 {status} Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
        }
    });
    int ms = await Latency.Http("http://target.invalid/generate_204", port, 2000, CancellationToken.None);
    bool redirectRejected = false;
    try { await Latency.Http("http://target.invalid/generate_204", port, 2000, CancellationToken.None); } catch (UserError ex) { redirectRejected = ex.Message == "HTTP 302"; }
    await proxy;
    Check(ms > 0 && requests.Count == 2 && requests.All(r => r.StartsWith("GET http://target.invalid/generate_204 HTTP/")), "HTTP ping uses the explicit proxy, no direct fallback");
    Check(redirectRejected, "HTTP redirects/captive portal are not successful connection checks");
}

using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
{
    listener.Start(); int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var stall = Task.Run(async () => { using var client = await listener.AcceptTcpClientAsync(); await Task.Delay(700); });
    var watch = System.Diagnostics.Stopwatch.StartNew(); bool timedOut = false;
    try { await Latency.Http("http://target.invalid/slow", port, 100, CancellationToken.None); } catch (OperationCanceledException) { timedOut = true; }
    Check(timedOut && watch.ElapsedMilliseconds < 650, "HTTP ping timeout cancels an actual stalled proxy request");
    await stall;
}
int sequence = 0;
var sampled = await Latency.Sample(new PingOptions { Attempts = 3 }, _ => ++sequence == 1 ? throw new TimeoutException() : Task.FromResult(sequence == 2 ? 100 : 40), CancellationToken.None);
Check(sampled.Ms == 70 && sampled.Successes == 2 && sampled.Attempts == 3 && sampled.Status == "ok", "median successful samples and loss count");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel(); bool stopped = false;
    try { await Latency.Sample(pingOptions, _ => Task.FromResult(99), cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
    Check(stopped, "cancelled ping never produces success");
}
int active = 0, peak = 0, finished = 0;
await Latency.Batch(Enumerable.Range(0, 12), 3, async (item, ct) =>
{
    int count = Interlocked.Increment(ref active); int previous;
    do { previous = peak; } while (count > previous && Interlocked.CompareExchange(ref peak, count, previous) != previous);
    await Task.Delay(20, ct); Interlocked.Decrement(ref active); Interlocked.Increment(ref finished);
}, CancellationToken.None);
Check(peak <= 3 && finished == 12 && active == 0, "batch limits simultaneous checks and completes all nodes");
using (var cancelBatch = new CancellationTokenSource(30))
{
    bool stopped = false;
    try { await Latency.Batch(Enumerable.Range(0, 50), 2, async (item, ct) => await Task.Delay(1000, ct), cancelBatch.Token); } catch (OperationCanceledException) { stopped = true; }
    Check(stopped, "batch propagates cancellation to active and queued checks");
}
var pingConfig = Configuration.Build(a.Nodes[0], 19500, false, [], tun: false);
Check(pingConfig["inbounds"]!.AsArray().Count == 1 && pingConfig["inbounds"]![0]!["type"]!.ToString() == "mixed" && pingConfig["route"]!["rules"]![2]!["outbound"]!.ToString() == "proxy", "HTTP ping config has no TUN and routes measurement inbound through the node");
foreach (var config in new[] { pingConfig, Configuration.Build(a.Nodes[0], 19501, false, []), Configuration.Build(a.Nodes[0], 19502, true, domains) })
{
    var servers = config["dns"]!["servers"]!.AsArray();
    var bootstrap = servers.Single(s => s!["tag"]!.ToString() == "bootstrap")!.AsObject();
    var remote = servers.Single(s => s!["tag"]!.ToString() == "remote")!;
    Check(!bootstrap.ContainsKey("detour") && bootstrap["server"]!.ToString() == "1.1.1.1"
        && bootstrap["tls"]!["enabled"]!.GetValue<bool>() && remote["detour"]!.ToString() == "proxy"
        && config["dns"]!["final"]!.ToString() == "remote" && config["route"]!["default_domain_resolver"]!.ToString() == "bootstrap",
        "startup regression: direct bootstrap avoids invalid empty-direct detour; normal DNS stays through proxy");
}
File.WriteAllText(Path.Combine(fixtureFolder, "ping.json"), pingConfig.ToJsonString());
var session = new SessionLog(); session.Write("launch", "CreateProcess stage");
try { throw new InvalidOperationException("actual failure", new IOException("inner failure")); } catch (Exception ex) { session.Error("launch", ex); }
Check(session.Snapshot().Contains("InvalidOperationException: actual failure") && session.Snapshot().Contains("IOException: inner failure") && session.Snapshot().Contains("Program.cs:line") && session.Snapshot().Contains("CreateProcess stage"), "full log preserves exception message, inner error, stack trace and launch stage");
var smallSession = new SessionLog(160); smallSession.Write("test", new string('x', 1000));
Check(smallSession.Snapshot().Contains("Начало журнала удалено") && smallSession.Snapshot().Length < 300, "full log memory bound explicitly reports truncation");
var otherNode = Subscriptions.ParseNode("trojan://second-secret@other.example:443#Second");
string manyClean = CoreDiagnostics.RedactMany("a@b:secret second-secret other.example example.com at NativeCore.cs:line 42", new[] { privateNode, otherNode });
Check(!manyClean.Contains("secret") && !manyClean.Contains("example") && manyClean.Contains("NativeCore.cs:line 42"), "full log redacts secrets of all tested servers while keeping stack location");

var oldProfile = new Profile { Name = "Old", Address = "https://subscription.example/sub", Nodes = a.Nodes, Selected = a.Nodes[0].Id, Theme = "light", Accent = "purple" };
oldProfile.Normalize(); string firstSubscription = oldProfile.ActiveSubscription;
Check(oldProfile.Subscriptions.Count == 1 && oldProfile.Nodes.Count == 1 && oldProfile.Selected == a.Nodes[0].Id, "legacy profile migration preserves nodes and selection");
oldProfile.Normalize(); Check(oldProfile.Subscriptions.Count == 1 && oldProfile.ActiveSubscription == firstSubscription, "profile migration is idempotent");
oldProfile.Favorites.Add(a.Nodes[0].Id); oldProfile.Selected = "auto"; oldProfile.CaptureActive();
var secondSubscription = new SavedSubscription { Name = "Second", Address = "https://other.example/sub", Nodes = [ws], Selected = ws.Id }; oldProfile.Subscriptions.Add(secondSubscription);
oldProfile.Activate(secondSubscription.Id); Check(oldProfile.Name == "Second" && oldProfile.Nodes.Single().Id == ws.Id && oldProfile.Favorites.Count == 0, "switch provider loads isolated nodes and preferences");
oldProfile.Activate(firstSubscription); Check(oldProfile.Selected == "auto" && oldProfile.Favorites.SequenceEqual(new[] { a.Nodes[0].Id }), "switch provider restores Auto and favorites");
oldProfile.DeferredUpdateVersion = "0.5.9";
var encrypted = Backup.Export(oldProfile, "correct-password");
Check(!Encoding.UTF8.GetString(encrypted).Contains(uuid) && !Encoding.UTF8.GetString(encrypted).Contains("subscription.example"), "backup contains no plaintext credentials");
var restoredProfile = Backup.Import(encrypted, "correct-password");
Check(restoredProfile.Subscriptions.Count == 2 && restoredProfile.Selected == "auto" && restoredProfile.Theme == "light" && restoredProfile.Accent == "purple", "encrypted backup restores all providers and preferences");
Check(restoredProfile.DeferredUpdateVersion == "0.5.9", "declined update survives encrypted profile persistence");
Check(JsonSerializer.Deserialize<Profile>("{}")!.DeferredUpdateVersion == "", "legacy profiles default to no declined update");
bool badPassword = false; try { Backup.Import(encrypted, "wrong-password"); } catch(UserError) { badPassword = true; } Check(badPassword, "backup rejects wrong password");
var damaged = encrypted.ToArray(); damaged[^1] ^= 1; bool damagedBackup = false; try { Backup.Import(damaged, "correct-password"); } catch(UserError) { damagedBackup = true; } Check(damagedBackup, "backup authentication rejects modified data");
bool tinyPassword = false; try { Backup.Export(oldProfile, "tiny"); } catch(UserError) { tinyPassword = true; } Check(tinyPassword, "backup requires minimum password");
var badOptions = new AutomationOptions { AutoMinutes = 0 }; bool badAutomation = false; try { badOptions.Validate(); } catch(UserError) { badAutomation = true; } Check(badAutomation, "invalid scheduler options rejected");
var policy = new ReconnectPolicy(); var moment = DateTimeOffset.UtcNow;
policy.Start(moment); Check(policy.Due(moment, true) && !policy.Due(moment, false), "reconnect waits for network");
policy.Retry(moment); Check(!policy.Due(moment.AddSeconds(1), true) && policy.Due(moment.AddSeconds(2), true), "reconnect initial delay");
for (int i = 0; i < 20; i++) policy.Retry(moment);
Check(policy.Failures == 10 && policy.Next == moment.AddSeconds(60), "reconnect backoff is bounded");
policy.Healthy(); Check(policy.Failures == 0, "healthy connection resets retry failures");
policy.Stop(); Check(!policy.Due(moment.AddDays(10), true), "manual stop never reconnects");
var automatic = Configuration.BuildAutomatic([a.Nodes[0], ws, ss], 19223, false, [], new(), 19224, "test-secret-012345678901234567890123456789");
var autoOutbounds = automatic["outbounds"]!.AsArray();
Check(autoOutbounds.Count == 5 && autoOutbounds[3]?["type"]?.ToString() == "urltest" && autoOutbounds[3]?["outbounds"]?.AsArray().Count == 3, "Auto configuration delegates full proxy checks to URLtest");
Check(autoOutbounds[3]?["interrupt_exist_connections"]?.GetValue<bool>() == false && automatic["experimental"]?["clash_api"]?["external_controller"]?.ToString() == "127.0.0.1:19224", "Auto API restricted to loopback and existing inbound sessions preserved");
Check(a.Nodes[0].Outbound["tag"]?.ToString() == "proxy", "Auto does not mutate stored outbounds");
File.WriteAllText(Path.Combine(fixtureFolder, "automatic.json"), automatic.ToJsonString());
foreach (string unsafePath in new[] { "../Kot.exe", "a/../../b", "C:/Kot.exe", "a\\b", "/absolute", "file:stream", "CON.txt", "LPT1", "a.", "a /b", "a//b", "a/", "a\n.exe" })
 Check(!ReleasePackage.SafeName(unsafePath), "update path rejected: " + unsafePath.Replace('\n', ' '));
Check(ReleasePackage.SafeName("ui/index.html") && ReleasePackage.SafeName("core/sing-box.exe"), "update permits normal relative files");
using (var rsa = System.Security.Cryptography.RSA.Create(2048))
{
    byte[] MakeRelease(bool corrupt = false, bool extra = false, bool singleFile = false)
    {
        var files = new Dictionary<string, byte[]> { ["Kot.exe"] = "binary-one"u8.ToArray(), ["Kot.dll"] = "binary-two"u8.ToArray(), ["core/sing-box.exe"] = "binary-three"u8.ToArray() };
        if (singleFile) files.Remove("Kot.dll");
        var hashes = files.ToDictionary(item => item.Key, item => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(item.Value)));
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest("0.2.1", hashes));
        byte[] signature = rsa.SignData(manifest, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] bytes) { using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
            Add("release.json", manifest); Add("release.sig", signature);
            foreach (var file in files) Add(file.Key, corrupt && file.Key == "Kot.exe" ? "damaged"u8.ToArray() : file.Value);
            if (extra) Add("ui/surprise.js", "unlisted"u8.ToArray());
        }
        return buffer.ToArray();
    }
    using var validZip = new System.IO.Compression.ZipArchive(new MemoryStream(MakeRelease()), System.IO.Compression.ZipArchiveMode.Read);
    Check(ReleasePackage.Validate(validZip, new Version("0.2.0"), rsa.ExportSubjectPublicKeyInfoPem()).Version == "0.2.1", "signed release validated with every hash");
    using var singleZip = new System.IO.Compression.ZipArchive(new MemoryStream(MakeRelease(singleFile: true)), System.IO.Compression.ZipArchiveMode.Read);
    Check(ReleasePackage.Validate(singleZip, new Version("0.2.0"), rsa.ExportSubjectPublicKeyInfoPem()).Version == "0.2.1", "signed single-file release does not require loose Kot.dll");
    bool sameVersion = false; try { ReleasePackage.Validate(validZip, new Version("0.2.1"), rsa.ExportSubjectPublicKeyInfoPem()); } catch(UserError) { sameVersion = true; } Check(sameVersion, "update cannot downgrade or reinstall same version");
    foreach (var kind in new[] { "corrupt", "extra", "signature" })
    {
        using var invalidZip = new System.IO.Compression.ZipArchive(new MemoryStream(MakeRelease(kind == "corrupt", kind == "extra")), System.IO.Compression.ZipArchiveMode.Read);
        bool invalidRelease = false; try { ReleasePackage.Validate(invalidZip, new Version("0.2.0"), kind == "signature" ? ReleaseKey.Public : rsa.ExportSubjectPublicKeyInfoPem()); } catch(UserError) { invalidRelease = true; }
        Check(invalidRelease, "update rejects " + kind);
    }
}
await Kot.Tests.BodyTests.Run(Check);
Kot.Tests.SetupTests.Run(Check);
Kot.Tests.InstallationTests.Run(Check, fixtureFolder);
await SecurityTests.Run(Check, vless, uuid);
await Kot.Tests.UpdateTests.Run(Check, fixtureFolder);
await Kot.Tests.ProductTests.Run(Check, a.Nodes[0], fixtureFolder);

sealed class FakeTransport(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
{
 public List<string> Addresses { get; } = [];
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { int index = Addresses.Count; Addresses.Add(request.RequestUri!.AbsoluteUri); return Task.FromResult(response(request, index)); }
}
