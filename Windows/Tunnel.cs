using Kot.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace Kot.Windows;
public sealed class Tunnel
{
    NativeCore? core;
    CancellationTokenSource? attempt;
    readonly SemaphoreSlim gate = new(1);
    public string State { get; private set; } = "idle";
    public string Error { get; private set; } = "";
    public string CoreDetails { get; private set; } = "";
    public int? CoreExitCode { get; private set; }
    Node? activeNode;
    public int ProxyPort { get; private set; }
    public int Session { get; private set; }
    int apiPort;
    string apiSecret = "";
    public string AutomaticNode { get; private set; } = "";
    public async Task<bool> Probe(CancellationToken ct)
    {
        int port = ProxyPort; if (State != "connected" || port == 0) return false;
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        foreach (string target in new[] { "https://www.gstatic.com/generate_204", "https://www.cloudflare.com/cdn-cgi/trace" })
        {
            try { using var r = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct); if (r.IsSuccessStatusCode) return true; }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested) { AppLog.Write("health", e.GetType().Name); }
        }
        return false;
    }
    public async Task ReadAutomaticNode(CancellationToken ct)
    {
        if (apiPort == 0 || State != "connected") return;
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiSecret);
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{apiPort}/proxies/proxy", ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            if (json.Length > 65536) return;
            using var doc = JsonDocument.Parse(json); AutomaticNode = doc.RootElement.TryGetProperty("now", out var value) ? value.GetString()?.Replace("node-", "") ?? "" : "";
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { AppLog.Write("auto", "selection read: " + e.GetType().Name); }
    }
    public async Task<ConnectionSnapshot?> ReadConnections(CancellationToken ct)
    {
        int port = apiPort, session = Session; string secret = apiSecret;
        if (port == 0 || State != "connected") return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/connections");
        request.Headers.Authorization = new("Bearer", secret);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new UserError("Слишком большой ответ локального API.");
        using var body = await response.Content.ReadAsStreamAsync(deadline.Token); using var buffer = new MemoryStream(); byte[] block = new byte[16384];
        while (true) { int read = await body.ReadAsync(block, deadline.Token); if (read == 0) break; if (buffer.Length + read > 2 * 1024 * 1024) throw new UserError("Слишком большой ответ локального API."); await buffer.WriteAsync(block.AsMemory(0, read), deadline.Token); }
        var value = ConnectionSnapshot.Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        return Session == session && State == "connected" ? value : null;
    }
    static int ReservePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    public event Action? Changed;
    readonly string path = Path.Combine(Store.Folder, "runtime.json");
    string CoreExe => Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe");
    public Tunnel() { try { File.Delete(path); } catch { } }
    void Set(string state, string error = "") { AppLog.Write("tunnel", "state=" + state + (error.Length > 0 ? "; " + error : "")); State = state; Error = error; Changed?.Invoke(); }
    public void Cancel() => attempt?.Cancel();
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
            AppLog.Write("tunnel", "connect: protocol=" + node.Protocol + "; node=" + node.Id + "; mode=" + profile.Mode);
            Session++; activeNode = node; CoreDetails = ""; CoreExitCode = null;
            attempt = new CancellationTokenSource(); var ct = attempt.Token;
            Set("connecting");
            using var portReservation = new TcpListener(IPAddress.Loopback, 0); portReservation.Start();
            int port = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
            AppLog.Write("tunnel", "build configuration");
            do { apiPort = ReservePort(); } while (apiPort == port);
            apiSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            AppLog.Secret(apiSecret);
            var candidates = profile.Favorites.Count > 0 ? profile.Nodes.Where(n => profile.Favorites.Contains(n.Id)).ToList() : profile.Nodes;
            if (candidates.Count == 0) candidates = profile.Nodes;
            var config = automatic ? Configuration.BuildAutomatic(candidates, port, profile.Mode == "smart", profile.Bypass, profile.Automation, apiPort, apiSecret)
                : Configuration.Build(node, port, profile.Mode == "smart", profile.Bypass);
            Configuration.Controller(config, apiPort, apiSecret);
            ProxyPort = port; AutomaticNode = "";
            Directory.CreateDirectory(Store.Folder);
            File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            // Validate against the bundled version before touching the network.
            var psi = new ProcessStartInfo(CoreExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(CoreExe)! };
            psi.ArgumentList.Add("check"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(path);
            AppLog.Write("tunnel", "sing-box check: starting");
            using (var check = Process.Start(psi) ?? throw new UserError("Не удалось запустить sing-box."))
            {
                var stderr = check.StandardError.ReadToEndAsync(ct); var stdout = check.StandardOutput.ReadToEndAsync(ct);
                try { await check.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct); }
                catch { if (!check.HasExited) check.Kill(true); throw; }
                await Task.WhenAll(stderr, stdout);
                AppLog.Write("check", "exit=" + check.ExitCode + "\n" + stderr.Result + "\n" + stdout.Result);
                if (check.ExitCode != 0)
                {
                    CoreExitCode = check.ExitCode;
                    CoreDetails = CoreDiagnostics.Tail(Clean(stderr.Result + "\n" + stdout.Result));
                    throw new UserError($"sing-box не принял конфигурацию (код {check.ExitCode}).\n" + CoreDiagnostics.Summary(CoreDetails));
                }
            }
            ct.ThrowIfCancellationRequested();
            AppLog.Write("tunnel", "sing-box run: starting");
            core = new NativeCore(CoreExe, path, text => AppLog.Write("tunnel-core", text));
            AppLog.Write("tunnel", "waiting for proxy health");
            using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            bool healthy = false; var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (core.HasExited) throw new UserError(await ExitReason(core));
                try
                {
                    string target = DateTime.UtcNow.Second % 2 == 0 ? "https://www.gstatic.com/generate_204" : "https://www.cloudflare.com/cdn-cgi/trace";
                    using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.IsSuccessStatusCode) { healthy = true; break; }
                }
                catch (HttpRequestException ex) { AppLog.Error("health", ex); }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { AppLog.Error("health", ex); }
                await Task.Delay(500, ct);
            }
            if (core.HasExited) throw new UserError(await ExitReason(core));
            if (!healthy)
            {
                CoreDetails = CoreDiagnostics.Tail(Clean(await core.ReadRecentOutput(false)));
                throw new UserError("Сервер не ответил на проверку соединения. " + (CoreDetails.Length > 0 ? CoreDiagnostics.Summary(CoreDetails) : "Попробуйте другой сервер."));
            }
            ct.ThrowIfCancellationRequested();
            try { File.Delete(path); } catch { }
            Set("connected");
            _ = Watch(core);
        }
        catch (Exception ex)
        {
            AppLog.Error("connect", ex);
            await StopCore(); Set("idle", ex is OperationCanceledException ? "" : Store.Friendly(ex));
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
        return $"sing-box завершился (код {CoreExitCode}, 0x{unchecked((uint)CoreExitCode.Value):X8}).\n" + CoreDiagnostics.Summary(CoreDetails);
    }
    async Task StopCore()
    {
        Session++; var old = core; core = null; ProxyPort = 0; apiPort = 0; apiSecret = ""; AutomaticNode = "";
        try { if (old != null) await old.Stop(); }
        catch (Exception ex) { AppLog.Error("stop core", ex); old?.Dispose(); }
        finally { attempt?.Dispose(); attempt = null; try { File.Delete(path); } catch { } }
    }
}
