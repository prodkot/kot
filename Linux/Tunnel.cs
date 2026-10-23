using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Kot.Core;
namespace Kot.Linux;

public sealed class Tunnel
{
    readonly GnomeProxy proxy;
    readonly XrayAssets assets;
    readonly GeoIpCache geo;
    OwnedProcess? core;
    CancellationTokenSource? attempt;
    readonly SemaphoreSlim gate = new(1);
    bool helper;
    bool recording;
    int metrics;
    int healthPort;
    string secret = "";
    public string State { get; private set; } = "idle";
    public string Error { get; private set; } = "";
    public string CoreDetails { get; private set; } = "";
    public int Port { get; private set; }
    public int HealthPort => State == "connected" ? healthPort : 0;
    public int Session { get; private set; }
    public long Started { get; private set; }
    public DateTimeOffset StartedUtc { get; private set; }
    public bool Guard { get; private set; }
    public string AutomaticNode { get; private set; } = "";
    public Action? Changed;
    public Action<int, ConnectionSnapshot?>? Ended;
    public static long Clock => (long)(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
    string GuardReceipt => Path.Combine(Paths.Config, "guard-active");
    public Tunnel(GnomeProxy proxy, XrayAssets assets, GeoIpCache geo) { this.proxy = proxy; this.assets = assets; this.geo = geo; Guard = File.Exists(GuardReceipt); }
    public void Cancel() => attempt?.Cancel();
    void Set(string state, string error = "") { State = state; Error = error; Changed?.Invoke(); }
    public async Task Prepare(IEnumerable<Node> nodes, CancellationToken ct)
    {
        var list = nodes.ToArray();
        if (list.All(assets.Available)) return;
        if (Guard && Port == 0) throw new UserError("Kill switch активен. Загрузки ждут подключения; защита не отключается автоматически.");
        using var http = new HttpClient(NetworkRequests.Handler(Port)) { Timeout = TimeSpan.FromSeconds(90) };
        await assets.Prepare(list, http, ct);
    }
    public async Task Validate(Node node, CancellationToken ct)
    {
        using var reserved = LoopbackPort.Reserve(ct);
        var config = XrayRuntime.Build(node, reserved.Port, platform: XrayPlatform.Linux);
        await Check(config, node, ct);
    }
    async Task Check(JsonObject config, Node node, CancellationToken ct)
    {
        await using var check = Start(config, assets.DirectoryFor(node), true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await check.Write(config.ToJsonString(), timeout.Token); check.CloseInput();
        await check.Wait(timeout.Token);
        if (check.ExitCode != 0) throw new UserError("Закреплённый Xray не поддерживает эту конфигурацию. " + CoreDiagnostics.Summary(CoreDiagnostics.CreateRedactor([node])(check.Log.Snapshot())));
    }
    static OwnedProcess Start(JsonObject config, string assets, bool test = false)
    {
        var process = new OwnedProcess(Paths.Core, test ? ["run", "-test", "-format", "json", "-c", "stdin:"] : ["run", "-format", "json", "-c", "stdin:"], assets);
        return process;
    }
    async Task<OwnedProcess> Launch(JsonObject config, string directory, bool privileged, bool guard, CancellationToken ct)
    {
        string? envelope = null;
        if (privileged)
        {
            if (AppContext.BaseDirectory.TrimEnd('/') != "/usr/lib/kot") throw new UserError("TUN запускается только из установленного root-owned пакета /usr/lib/kot.");
            var files = new Dictionary<string, string>();
            foreach (string name in new[] { "geoip.dat", "geosite.dat" })
                if (File.Exists(Path.Combine(directory, name)))
                {
                    if (new FileInfo(Path.Combine(directory, name)).Length > XrayAssets.MaxBytes) throw new UserError("Геобаза слишком большая.");
                    files[name] = Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(directory, name), ct));
                }
            envelope = System.Text.Json.JsonSerializer.Serialize(new { config, guard, assets = files }) + "\n";
        }
        ct.ThrowIfCancellationRequested();
        var process = privileged ? new OwnedProcess("/usr/bin/pkexec", ["/usr/lib/kot/network-helper.py"]) : Start(config, directory);
        try { await process.Write(envelope ?? config.ToJsonString(), ct); if (!privileged) process.CloseInput(); return process; }
        catch { process.CloseInput(); await process.DisposeAsync(); throw; }
    }
    public async Task<int> Measure(Node node, string target, int timeout, CancellationToken ct)
    {
        if (State == "connected" && helper) throw new UserError("Изолированные HTTP-пробы во время TUN пока не поддерживаются. Отключите TUN перед проверкой.");
        await Prepare([node], ct);
        using var reserved = LoopbackPort.Reserve(ct);
        int port = reserved.Port;
        var config = XrayRuntime.Build(node, port, platform: XrayPlatform.Linux);
        reserved.Dispose();
        await using var probe = await Launch(config, assets.DirectoryFor(node), false, false, ct);
        for (int i = 0; i < 50; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (probe.Exited) throw new UserError("Xray завершил проверку с ошибкой.");
            try { await Latency.Tcp("127.0.0.1", port, 100, ct); return await Latency.Http(target, port, timeout, ct); }
            catch (System.Net.Sockets.SocketException) { }
            await Task.Delay(100, ct);
        }
        throw new UserError("Порт проверки не открылся.");
    }
    public async Task Connect(Profile profile, string? healthTarget = null)
    {
        await gate.WaitAsync();
        try
        {
            if (State != "idle") return;
            attempt = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var ct = attempt.Token;
            Set("connecting");
            var node = profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected);
            if (profile.Selected == "auto")
            {
                string selected = await AutomaticSelection.Select(profile, "", (n, token) => Measure(n, "https://www.gstatic.com/generate_204", 4000, token), ct);
                node = profile.Nodes.Single(n => n.Id == selected); AutomaticNode = selected;
            }
            if (node == null) throw new UserError("Добавьте подписку и выберите сервер.");
            await Prepare([node], ct);
            var rules = profile.ActiveRoutingProfile.Length == 0 ? null : profile.RoutingProfiles.Single(p => p.Id == profile.ActiveRoutingProfile).Rules;
            string[] countries = rules?.Countries() ?? [];
            await geo.Update(countries, true, (path, token) => GeoIp.ValidateFile(path, Paths.Core, token), ct,
                (code, token) => GeoIp.Download(code, token, NetworkRequests.Handler(Port)));
            using var p1 = LoopbackPort.Reserve(ct); using var p2 = LoopbackPort.Reserve(ct); using var p3 = LoopbackPort.Reserve(ct);
            int health = p1.Port, system = p2.Port; metrics = p3.Port;
            healthPort = health;
            secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var config = XrayRuntime.Build(node, health, profile.ConnectionMode == "tun", system, metrics, secret,
                profile.Mode == "smart", profile.Bypass, rules, geo.Paths(countries), platform: XrayPlatform.Linux);
            await Check(config, node, ct);
            helper = profile.ConnectionMode == "tun" || profile.KillSwitch;
            if (profile.KillSwitch) { Paths.Atomic(GuardReceipt, "Protection may be active; explicit recovery is required after a crash.\n"u8.ToArray()); Guard = true; }
            p1.Dispose(); p2.Dispose(); p3.Dispose();
            core = await Launch(config, assets.DirectoryFor(node), helper, profile.KillSwitch, ct);
            using var handler = NetworkRequests.Handler(health); using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
            bool healthy = false;
            for (int i = 0; i < 12; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (core.Exited) throw new UserError("Xray/helper завершился. Проверьте polkit и зависимости; параметры подключения в журнал не выводятся.");
                try
                {
                    using var response = await client.GetAsync(healthTarget ?? "https://www.gstatic.com/generate_204", HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        if (profile.ConnectionMode == "tun")
                        {
                            using var dnsDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct); dnsDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                            if ((await Dns.GetHostAddressesAsync(new Uri(healthTarget ?? "https://www.gstatic.com").Host, dnsDeadline.Token)).Length == 0) continue;
                        }
                        healthy = true; break;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested) { }
                await Task.Delay(500, ct);
            }
            if (!healthy)
            {
                CoreDetails = CoreDiagnostics.CreateRedactor([node])(core.Log.Snapshot());
                throw new UserError("Сервер не прошёл реальную проверку через VPN. " + CoreDiagnostics.Summary(CoreDetails));
            }
            if (profile.ConnectionMode == "proxy") await proxy.Enable(system);
            Port = system; Session++; Started = Clock; StartedUtc = DateTimeOffset.UtcNow;
            recording = true;
            Set("connected");
        }
        catch (Exception ex)
        {
            await StopCore(false);
            Set("idle", ex is OperationCanceledException ? "Подключение отменено или истекло время ожидания." : ex is UserError ? ex.Message : "Ошибка подключения: " + ex.GetType().Name);
        }
        finally { attempt?.Dispose(); attempt = null; gate.Release(); }
    }
    public async Task<ConnectionSnapshot?> Metrics(CancellationToken ct)
    {
        if (State is not ("connected" or "disconnecting")) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var handler = new HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:" + metrics) { Credentials = new NetworkCredential("kot", secret) }, UseProxy = true, AllowAutoRedirect = false };
        using var http = new HttpClient(handler);
        using var response = await http.GetAsync("http://kot-metrics.internal/debug/vars", deadline.Token);
        response.EnsureSuccessStatusCode();
        return ConnectionSnapshot.ParseXray(await BoundedBody.ReadUtf8(response.Content, 2 * 1024 * 1024, deadline.Token));
    }
    public async Task<bool> Probe(CancellationToken ct)
    {
        if (State != "connected" || healthPort == 0) return false;
        try { await Latency.Http("https://www.gstatic.com/generate_204", healthPort, 4000, ct); return true; }
        catch (Exception e) when (e is HttpRequestException or UserError || e is OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }
    public async Task Watch()
    {
        if (State != "connected" || core?.Exited != true) return;
        await gate.WaitAsync();
        try { if (State == "connected" && core?.Exited == true) { await StopCore(false); Set("idle", "Ядро завершилось. Защита kill switch, если включена, сохранена."); } }
        finally { gate.Release(); }
    }
    public async Task Stop(bool normal = true)
    {
        Cancel(); await gate.WaitAsync();
        try { Set("disconnecting"); await StopCore(normal); Set("idle"); }
        finally { gate.Release(); }
    }
    async Task StopCore(bool normal)
    {
        ConnectionSnapshot? last = null;
        if (recording) { try { last = await Metrics(CancellationToken.None); } catch { } recording = false; Ended?.Invoke(Session, last); }
        var old = core; core = null; Port = 0;
        try { await proxy.Restore(); }
        finally
        {
            if (old != null)
            {
                if (helper && !old.Exited)
                {
                    if (normal) await old.Write("stop\n", CancellationToken.None);
                    old.CloseInput();
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await old.Wait(wait.Token);
                }
                try { if (helper && normal && old.ExitCode != 0) throw new UserError("Helper не подтвердил восстановление сети. Kill switch считается активным; выполните явное восстановление."); }
                finally { await old.DisposeAsync(); }
            }
            if (normal && Guard && old == null)
                await OwnedProcess.Run("/usr/bin/pkexec", ["/usr/lib/kot/network-helper.py", "recover"]);
            if (normal) { Guard = false; File.Delete(GuardReceipt); }
        }
    }
}
