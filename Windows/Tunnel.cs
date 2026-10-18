using Kot.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace Kot.Windows;
public sealed class Tunnel
{
    NativeCore? core;
    public bool HasCore => core != null;
    CancellationTokenSource? attempt;
    bool cancelled;
    bool usesTun;
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
        int port = ProxyPort, session = Session; bool tun = usesTun;
        if (State != "connected" || port == 0) return false;
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        foreach (string target in healthTargets)
        {
            try { using var r = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct); if (r.IsSuccessStatusCode && (!tun || await TunnelDns.Probe(target, ct))) return State == "connected" && Session == session; }
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
        string selected;
        try { selected = await SelectAutomatic(profile, ct); }
        catch (UserError ex)
        {
            if (Session == session && State == "connected") { await Stop(); Set("idle", ex.Message); }
            return false;
        }
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
        return await AutomaticSelection.Select(profile, AutomaticNode, async (node, token) =>
        {
            try
            {
                await using var probe = await ProbeCore.Start(node, token);
                return await Latency.Http(healthTargets[0], probe.Port, 4000, token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { AppLog.Write("auto", node.Id + ": " + ex.GetType().Name); throw; }
        }, ct);
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
    string CoreExe => Path.Combine(AppContext.BaseDirectory, "core", "xray.exe");
    public Tunnel() : this(DefaultHealthTargets, TimeSpan.FromSeconds(30)) { }
    internal Tunnel(string[] targets, TimeSpan timeout)
    {
        healthTargets = targets; healthTimeout = timeout;
        // Remove the previous version's single leftover credential file once.
        try { File.Delete(Path.Combine(Store.Folder, "runtime.json")); } catch { }
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
            if (automatic) node = AutomaticSelection.Candidates(profile).FirstOrDefault() ?? throw new UserError(AutomaticSelection.NoCandidates);
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
                node = AutomaticSelection.Candidates(profile).FirstOrDefault(n => n.Id == selected)
                    ?? throw new UserError(AutomaticSelection.NoCandidates);
            }
            activeNode = node; lastAutomatic = MonotonicMilliseconds;
            var routing = profile.ActiveRoutingProfile.Length == 0 ? null : profile.RoutingProfiles.FirstOrDefault(p => p.Id == profile.ActiveRoutingProfile)?.Rules ?? throw new UserError("Профиль маршрутизации удалён.");
            var config = XrayRuntime.Build(node, port, profile.ConnectionMode == "tun", systemPort, apiPort, apiSecret,
                profile.Mode == "smart", profile.Bypass, routing, GeoIpStore.Cache.Paths(countries));
            usesTun = profile.ConnectionMode == "tun";
            ProxyPort = port; AutomaticNode = automatic ? node.Id : "";
            string json = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await XraySession.Check(CoreExe, json, ProviderAssets.Cache.DirectoryFor(node), Clean, ct);
            ct.ThrowIfCancellationRequested();
            portReservation.Dispose();
            core = XraySession.Start(CoreExe, json, ProviderAssets.Cache.DirectoryFor(node), "kot. Xray", text => AppLog.Write("tunnel-core", text));
            AppLog.Write("tunnel", "waiting for proxy health");
            using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            bool healthy = false, proxyHealthy = false, tunPermitted = false; int healthIndex = 0;
            using var healthDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct); healthDeadline.CancelAfter(healthTimeout);
            while (!healthDeadline.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                if (core.HasExited) throw new UserError(await ExitReason(core));
                try
                {
                    string target = healthTargets[healthIndex++ % healthTargets.Length];
                    using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, healthDeadline.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        proxyHealthy = true;
                        if (usesTun)
                        {
                            // DNS Client runs outside Xray. Allow the verified TUN
                            // interface before checking resolution with the guard on.
                            if (profile.KillSwitch && !tunPermitted) { KillSwitch.Arm(KillSwitch.TunnelInterface()); tunPermitted = true; }
                            if (!await TunnelDns.Probe(target, healthDeadline.Token)) { await Task.Delay(250, healthDeadline.Token); continue; }
                        }
                        healthy = true; break;
                    }
                }
                catch (HttpRequestException ex) { AppLog.Error("health", ex); }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { AppLog.Error("health", ex); }
                await Task.Delay(500, ct);
            }
            ct.ThrowIfCancellationRequested();
            if (core.HasExited) throw new UserError(await ExitReason(core));
            if (!healthy)
            {
                CoreDetails = CoreDiagnostics.Tail(Clean(await core.ReadRecentOutput(false)));
                if (usesTun && proxyHealthy) throw new UserError("Сервер доступен, но DNS Windows через туннель не отвечает. Переподключитесь или проверьте настройки DNS.");
                throw new UserError("Сервер не ответил на проверку соединения. " + (CoreDetails.Length > 0 ? CoreDiagnostics.Summary(CoreDetails) : "Попробуйте другой сервер."));
            }
            ct.ThrowIfCancellationRequested();
            if (profile.ConnectionMode == "proxy") SystemProxy.Enable(systemPort);
            if (profile.KillSwitch) KillSwitch.Arm(profile.ConnectionMode == "tun" ? KillSwitch.TunnelInterface() : 0);
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
        Session++; var old = core; core = null; ProxyPort = 0; apiPort = 0; apiSecret = ""; AutomaticNode = ""; usesTun = false;
        try
        {
            try { SystemProxy.Restore(); }
            finally
            {
                if (old != null) await StopProcess(old); else { attempt?.Dispose(); attempt = null; }
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
        finally { attempt?.Dispose(); attempt = null; }
    }
}
