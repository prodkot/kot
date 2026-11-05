using Kot.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kot.Windows;

sealed class Pause
{
    public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task Wait(CancellationToken ct) { Entered.TrySetResult(); await Continue.Task.WaitAsync(ct); }
}
static class Fixture
{
    public static Pause? Health;
    public static void Reset()
    {
        Health = null; ProviderAssets.Pause = null; XraySession.Pause = null; XraySession.FailCheck = false; XraySession.ValidationPause = null;
        KillSwitch.Active = false; Store.FailSave = false;
    }
}
sealed class FakeProxy : IDisposable
{
    readonly TcpListener listener;
    readonly CancellationTokenSource stop = new();
    readonly Pause? pause;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public FakeProxy(int port = 0, Pause? pause = null)
    {
        this.pause = pause; listener = new(IPAddress.Loopback, port); listener.Start(); _ = Accept();
    }
    async Task Accept()
    {
        try { while (!stop.IsCancellationRequested) _ = Respond(await listener.AcceptTcpClientAsync(stop.Token)); }
        catch (OperationCanceledException) { }
    }
    async Task Respond(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                if (pause != null) await pause.Wait(stop.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"), stop.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); stop.Dispose(); }
}
public sealed partial class MainWindow
{
    Profile profile;
    readonly Tunnel tunnel;
    readonly CancellationTokenSource lifetime = new();
    readonly PingFixture ping = new();
    readonly SemaphoreSlim operations = new(1);
    bool connecting => false;
    bool exiting => false;
    string geoIpError { get; set; } = "";
    DateTimeOffset? geoIpAttempt { get; set; }
    List<string> warnings = [];
    int RequestProxyPort => 0;
    int GeoIpProxyPort => 0;
    string[] GeoIpCountries() => [];
    internal Pause? ToolsPause;
    public MainWindow(Profile profile)
    {
        this.profile = profile;
        var signature = new[] { typeof(string[]), typeof(TimeSpan), typeof(Func<bool>) };
        var constructor = typeof(Tunnel).GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, signature);
        tunnel = constructor == null ? new(["http://health.invalid/"], TimeSpan.FromSeconds(3))
            : (Tunnel)constructor.Invoke([new[] { "http://health.invalid/" }, TimeSpan.FromSeconds(3), (Func<bool>)(() => this.profile.KillSwitch)]);
    }
    public bool SavedGuard => profile.KillSwitch;
    public Tunnel Connection => tunnel;
    public Profile Saved => profile;
    public Task Begin() => tunnel.Connect(profile);
    public Task ChangeGuard(bool value) => ChangeSetting(JsonSerializer.SerializeToElement(new { key = "killSwitch", value }));
    void SaveProfile(Action<Profile> edit) => profile = Store.Change(profile, edit);
    Task StopTools() => ToolsPause?.Wait(CancellationToken.None) ?? Task.CompletedTask;
    void ResumeDesired() { }
    public Task LocalImport(string text, CancellationToken ct) => ApplyLocalImport(text, "new", ct);
    public Task Refresh(string address, SubscriptionResult result, CancellationToken ct) => ApplySubscription(address, "subscription", result, false, ct);
    public void RefreshInactive(string id, SubscriptionResult result) => ApplyInactiveSubscription(id, result);
}
sealed class PingFixture { public Task Stop(bool clear = false) => Task.CompletedTask; }
sealed class SaveFileDialog : IDisposable
{
    public string Filter = "", FileName = "";
    public DialogResult ShowDialog(object owner) => throw new NotSupportedException();
    public void Dispose() { }
}
enum DialogResult { OK }
static class Store
{
    public static string Folder = Path.Combine(Path.GetTempPath(), "kot-transitions-" + Guid.NewGuid().ToString("N"));
    public static bool FailSave;
    static ProfilePersistence Persistence => new(Folder, bytes => JsonSerializer.Deserialize<Profile>(bytes)!, value => JsonSerializer.SerializeToUtf8Bytes(value));
    public static Profile Change(Profile profile, Action<Profile> edit)
    {
        if (FailSave) throw new IOException("simulated persistence failure");
        return Persistence.Change(profile, edit);
    }
    public static void Save(Profile profile) { if (FailSave) throw new IOException("simulated persistence failure"); Persistence.Save(profile); }
    public static string Friendly(Exception e) => e.Message;
}
static class KillSwitch
{
    public static bool Active;
    public static ulong Interface;
    public static void Arm(ulong tun = 0) { Active = true; Interface = tun; }
    public static void Release() { Active = false; Interface = 0; }
    public static ulong TunnelInterface() => 42;
}
static class ProviderAssets
{
    public static Pause? Pause;
    public static XrayAssets Cache = new(Path.Combine(Store.Folder, "assets"));
    public static Task Prepare(IEnumerable<Node> nodes, CancellationToken ct, int proxyPort = 0) => Pause?.Wait(ct) ?? Task.CompletedTask;
}
static class GeoIpStore
{
    public static FakeGeo Cache = new();
    public static Task Update(string[] countries, bool update, CancellationToken ct, int proxyPort = 0) => Task.CompletedTask;
}
sealed class FakeGeo { public Dictionary<string, string> Paths(string[] countries) => []; }
static class XraySession
{
    public static Pause? Pause;
    public static bool FailCheck;
    public static Pause? ValidationPause;
    public static async Task<SubscriptionResult> Validate(SubscriptionResult result, CancellationToken ct)
    {
        // Model a native response which has already passed cancellation to
        // confirm that production checks again before stopping/saving.
        if (ValidationPause != null) await ValidationPause.Wait(CancellationToken.None);
        return result;
    }
    public static async Task Check(string exe, string config, string assets, Func<string, string> clean, CancellationToken ct)
    {
        if (Pause != null) await Pause.Wait(ct);
        if (FailCheck) throw new UserError("simulated core rejection");
    }
    public static NativeCore Start(string exe, string config, string assets, string title, Action<string> report)
        => new(JsonNode.Parse(config)!["inbounds"]![0]!["port"]!.GetValue<int>());
}
public sealed class NativeCore : IDisposable
{
    readonly FakeProxy proxy;
    readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public NativeCore(int port) { proxy = new(port, Fixture.Health); }
    public bool HasExited => stopped.Task.IsCompleted;
    public int ExitCode => 0;
    public Task<string> ReadRecentOutput(bool wait) => Task.FromResult("");
    public Task WaitForExitAsync() => stopped.Task;
    public Task Stop() { Dispose(); return Task.CompletedTask; }
    public void Dispose() { if (!stopped.Task.IsCompleted) { proxy.Dispose(); stopped.TrySetResult(); } }
}
sealed class ProbeCore : IAsyncDisposable
{
    public static string Allowed = "";
    public static int PortForTests;
    public int Port => PortForTests;
    public static Task<ProbeCore> Start(Node node, CancellationToken ct)
        => node.Id == Allowed ? Task.FromResult(new ProbeCore()) : throw new UserError("unavailable probe");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
static class PhysicalNetwork { public static void Capture() { } }
static class SystemProxy { public static void Enable(int port) { } public static void Restore() { } }
static class TunnelDns { public static Task<bool> Probe(string target, CancellationToken ct) => Task.FromResult(true); }
static class AppLog
{
    public static void Register(Profile profile) { }
    public static void Write(string area, string text) { }
    public static void Error(string area, Exception error) { }
    public static string Clean(string text) => text;
}
static class Startup { public static Task Set(bool value) => Task.CompletedTask; }
