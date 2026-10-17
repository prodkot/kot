using Kot.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace Kot.Windows;
public sealed class Tunnel
{
    NativeCore? core;
    public bool UsesXhttp => core != null;
    CancellationTokenSource? attempt;
    bool cancelled;
    int stopGeneration;
    static readonly string[] DefaultHealthTargets = ["https://www.gstatic.com/generate_204", "https://www.cloudflare.com/cdn-cgi/trace"];
    readonly string[] healthTargets;
    readonly TimeSpan healthTimeout;
    readonly SemaphoreSlim gate = new(1);
    public string State { get; private set; } = "idle";
    public string Error { get; private set; } = "";
    public string CoreDetails { get; private set; } = "";
    public int? CoreExitCode { get; private set; }
    Node? activeNode;
    public int ProxyPort { get; private set; }
    public int Session { get; private set; }
    public long ConnectedMilliseconds { get; private set; }
    public DateTimeOffset ConnectedUtc { get; private set; }
    public event Action<int, long, DateTimeOffset, ConnectionSnapshot?>? SessionEnded;
    public static long MonotonicMilliseconds => (long)(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
    bool statisticsStarted;
    int apiPort;
    string apiSecret = "";
    Profile? automaticProfile;
    long lastAutomatic;
    string preferredNode = "";
    public string AutomaticNode { get; private set; } = "";
    public async Task<bool> Probe(CancellationToken ct)
    {
        int port = ProxyPort; if (State != "connected" || port == 0) return false;
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        foreach (string target in healthTargets)
        {
            try { using var r = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct); if (r.IsSuccessStatusCode) return true; }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested) { AppLog.Write("health", e.GetType().Name); }
        }
        return false;
    }
    public async Task<bool> ReadAutomaticNode(CancellationToken ct)
    {
        var profile = automaticProfile;
        if (profile == null || State != "connected") return false;
        if (MonotonicMilliseconds - lastAutomatic < profile.Automation.AutoMinutes * 60000L) return true;
        int generation = stopGeneration;
        int session = Session; lastAutomatic = MonotonicMilliseconds;
        string selected = await SelectAutomatic(profile, ct);
        if (Session != session || State != "connected") return false;
        if (selected != AutomaticNode)
        {
            preferredNode = selected;
            await Stop();
            if (!ct.IsCancellationRequested && stopGeneration == generation + 1) await Connect(profile);
        }
        return State == "connected";
    }
    async Task<string> SelectAutomatic(Profile profile, CancellationToken ct)
    {
        var nodes = Xhttp.ConnectionNodes(profile);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(20));
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        try
        {
            await Parallel.ForEachAsync(nodes.OrderBy(n => n.Id == AutomaticNode ? 0 : 1), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token }, async (node, token) =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(6));
                try
                {
                    await using var probe = await ProbeCore.Start(node, deadline.Token);
                    results[node.Id] = await Latency.Http(healthTargets[0], probe.Port, 4000, deadline.Token);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Write("auto", node.Id + ": " + ex.GetType().Name); }
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();
        string fallback = nodes.Any(n => n.Id == AutomaticNode) ? AutomaticNode : nodes[0].Id;
        var best = results.OrderBy(p => p.Value).FirstOrDefault();
        if (best.Key == null) return fallback;
        return results.TryGetValue(AutomaticNode, out int current) && current <= best.Value + profile.Automation.AutoToleranceMs ? AutomaticNode : best.Key;
    }
    async Task<string> ReadMetrics(int port, string secret, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { UseProxy = true, Proxy = new WebProxy("http://127.0.0.1:" + port) { Credentials = new NetworkCredential("kot", secret) }, AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync("http://kot-metrics.internal/debug/vars", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await BoundedBody.ReadUtf8(response.Content, 2 * 1024 * 1024, ct);
    }
    public Task<ConnectionSnapshot?> ReadConnections(CancellationToken ct) => ReadConnections(ct, false);
    async Task<ConnectionSnapshot?> ReadConnections(CancellationToken ct, bool closing)
    {
        int port = apiPort, session = Session; string secret = apiSecret;
        bool CanRead() => State == "connected" || closing && State == "disconnecting";
        if (port == 0 || !CanRead()) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var value = ConnectionSnapshot.ParseXray(await ReadMetrics(port, secret, deadline.Token));
        return Session == session && CanRead() ? value : null;
    }
    static int ReservePort() { using var reserved = LoopbackPort.Reserve(); return reserved.Port; }
    public event Action? Changed;
    readonly string path = Path.Combine(Store.Folder, "runtime.json");
    string CoreExe => Path.Combine(AppContext.BaseDirectory, "core", "xray.exe");
    public Tunnel() : this(DefaultHealthTargets, TimeSpan.FromSeconds(30)) { }
    internal Tunnel(string[] targets, TimeSpan timeout)
    {
        healthTargets = targets; healthTimeout = timeout;
        try { File.Delete(path); } catch { }
    }
    void Set(string state, string error = "") { AppLog.Write("tunnel", "state=" + state + (error.Length > 0 ? "; " + error : "")); State = state; Error = error; Changed?.Invoke(); }
    public void Cancel() { Interlocked.Increment(ref stopGeneration); cancelled = true; attempt?.Cancel(); }
    public async Task Toggle(Profile profile)
    {
        if (State != "idle") { await Stop(); return; }
        await Connect(profile);
    }
    public async Task Connect(Profile profile)
    {
        await gate.WaitAsync();
        try
        {
            if (core != null || State != "idle") return;
            Node? node = profile.Nodes.FirstOrDefault(n => n.Id == profile.Selected);
            bool automatic = profile.Selected == "auto";
            if (automatic) node = profile.Nodes.FirstOrDefault();
            if (node == null) throw new UserError("Сначала добавьте подписку и выберите сервер.");
            AppLog.Register(profile);
            AppLog.Write("tunnel", "connect: protocol=" + node.Protocol + "; node=" + node.Id + "; mode=" + profile.ConnectionMode);
            PhysicalNetwork.Capture();
            Session++; activeNode = node; CoreDetails = ""; CoreExitCode = null;
            cancelled = false; attempt = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = attempt.Token;
            Set("connecting");
            await ProviderAssets.Prepare(Xhttp.ConnectionNodes(profile), ct);
            string[] countries = profile.RoutingProfiles.FirstOrDefault(p => p.Id == profile.ActiveRoutingProfile)?.Rules.Countries() ?? [];
            await GeoIpStore.Update(countries, true, ct);
            if (profile.KillSwitch) KillSwitch.Arm(); else if (KillSwitch.Active) KillSwitch.Release();
            ct.ThrowIfCancellationRequested();
            using var portReservation = LoopbackPort.Reserve(ct);
            int port = portReservation.Port;
            AppLog.Write("tunnel", "build configuration");
            do { apiPort = ReservePort(); } while (apiPort == port);
            apiSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            int systemPort = 0;
            if (profile.ConnectionMode == "proxy") do { systemPort = ReservePort(); } while (systemPort == port || systemPort == apiPort);
            automaticProfile = automatic ? profile : null;
            if (automatic)
            {
                string selected = preferredNode.Length > 0 ? preferredNode : await SelectAutomatic(profile, ct);
                preferredNode = "";
                node = profile.Nodes.FirstOrDefault(n => n.Id == selected) ?? node;
            }
            activeNode = node; lastAutomatic = MonotonicMilliseconds;
            var routing = profile.ActiveRoutingProfile.Length == 0 ? null : profile.RoutingProfiles.FirstOrDefault(p => p.Id == profile.ActiveRoutingProfile)?.Rules ?? throw new UserError("Профиль маршрутизации удалён.");
            var config = XrayRuntime.Build(node, port, profile.ConnectionMode == "tun", systemPort, apiPort, apiSecret,
                profile.Mode == "smart", profile.Bypass, routing, GeoIpStore.Cache.Paths(countries));
            ProxyPort = port; AutomaticNode = automatic ? node.Id : "";
            Directory.CreateDirectory(Store.Folder);
            File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            await XraySession.Check(CoreExe, path, ProviderAssets.Cache.DirectoryFor(node), Clean, ct);
            ct.ThrowIfCancellationRequested();
            portReservation.Dispose();
            core = new NativeCore(CoreExe, ["run", "-c", path], Path.GetDirectoryName(CoreExe)!, "kot. Xray", text => AppLog.Write("tunnel-core", text), ProviderAssets.Cache.DirectoryFor(node));
            AppLog.Write("tunnel", "waiting for proxy health");
            using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            bool healthy = false; int healthIndex = 0;
            using var healthDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct); healthDeadline.CancelAfter(healthTimeout);
            while (!healthDeadline.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                if (core.HasExited) throw new UserError(await ExitReason(core));
                try
                {
                    string target = healthTargets[healthIndex++ % healthTargets.Length];
                    using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, healthDeadline.Token);
                    if (response.IsSuccessStatusCode) { healthy = true; break; }
                }
                catch (HttpRequestException ex) { AppLog.Error("health", ex); }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { AppLog.Error("health", ex); }
                await Task.Delay(500, ct);
            }
            ct.ThrowIfCancellationRequested();
            if (core.HasExited) throw new UserError(await ExitReason(core));
            if (!healthy)
            {
                CoreDetails = CoreDiagnostics.Tail(Clean(await core.ReadRecentOutput(false)));
                throw new UserError("Сервер не ответил на проверку соединения. " + (CoreDetails.Length > 0 ? CoreDiagnostics.Summary(CoreDetails) : "Попробуйте другой сервер."));
            }
            ct.ThrowIfCancellationRequested();
            try { File.Delete(path); } catch { }
            if (profile.ConnectionMode == "proxy") SystemProxy.Enable(systemPort);
            if (profile.KillSwitch) KillSwitch.Arm(profile.ConnectionMode == "tun" ? KillSwitch.TunnelInterface() : 0, UsesXhttp);
            attempt.CancelAfter(Timeout.InfiniteTimeSpan);
            ConnectedMilliseconds = MonotonicMilliseconds; ConnectedUtc = DateTimeOffset.UtcNow; statisticsStarted = true;
            Set("connected");
            _ = Watch(core);
        }
        catch (Exception ex)
        {
            AppLog.Error("connect", ex);
            bool timedOut = ex is OperationCanceledException && !cancelled && attempt?.IsCancellationRequested == true;
            string error = timedOut ? "Подключение не завершилось за 60 секунд. Проверьте сервер или выберите другой." : ex is OperationCanceledException ? "" : Store.Friendly(ex);
            await StopCore(); Set("idle", error);
        }
        finally { gate.Release(); }
    }
    async Task Watch(NativeCore observed)
    {
        try { await observed.WaitForExitAsync(); } catch { return; }
        await gate.WaitAsync();
        try { if (core == observed) { string reason = await ExitReason(observed); await StopCore(); Set("idle", reason); } }
        finally { gate.Release(); }
    }
    public async Task Stop()
    {
        Cancel(); await gate.WaitAsync();
        try { if (core != null) Set("disconnecting"); await StopCore(); Set("idle"); }
        finally { gate.Release(); }
    }
    string Clean(string text) => AppLog.Clean(CoreDiagnostics.Redact(text, activeNode, Store.Folder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    async Task<string> ExitReason(NativeCore observed)
    {
        CoreDetails = CoreDiagnostics.Tail(Clean(await observed.ReadRecentOutput(true)));
        CoreExitCode = observed.ExitCode;
        AppLog.Write("tunnel", "unexpected exit=" + CoreExitCode);
        return $"Xray завершился (код {CoreExitCode}, 0x{unchecked((uint)CoreExitCode.Value):X8}).\n" + CoreDiagnostics.Summary(CoreDetails);
    }
    async Task StopCore()
    {
        if (statisticsStarted)
        {
            long ended = MonotonicMilliseconds; DateTimeOffset utc = DateTimeOffset.UtcNow;
            ConnectionSnapshot? final = null;
            try { if (core != null && !core.HasExited) final = await ReadConnections(CancellationToken.None, true); }
            catch (Exception ex) { AppLog.Write("telemetry", "final sample: " + ex.GetType().Name); }
            statisticsStarted = false;
            SessionEnded?.Invoke(Session, ended, utc, final);
        }
        Session++; var old = core; core = null; ProxyPort = 0; apiPort = 0; apiSecret = ""; AutomaticNode = "";
        try
        {
            try { SystemProxy.Restore(); }
            finally
            {
                if (old != null) await StopProcess(old); else { attempt?.Dispose(); attempt = null; try { File.Delete(path); } catch { } }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("restore system proxy", ex);
            Set("idle", "Не удалось восстановить настройки прокси Windows. " + Store.Friendly(ex));
            throw;
        }
    }
    async Task StopProcess(NativeCore old)
    {
        try { await old.Stop(); }
        catch (Exception ex) { AppLog.Error("stop core", ex); old?.Dispose(); }
        finally { attempt?.Dispose(); attempt = null; try { File.Delete(path); } catch { } }
    }
}
