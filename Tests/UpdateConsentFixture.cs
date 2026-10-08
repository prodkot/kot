// Compile the production updater orchestration with deterministic network, UI and
// installation services. No executable or updater is launched by these tests.
using Kot.Core;
using System.Text.Json;
namespace Kot.Windows;

public sealed partial class MainWindow
{
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim operations = new(1);
    Profile profile = new();
    bool connecting = false, exiting = false;
    bool IsDisposed => false;
    bool IsHandleCreated => true;
    int installs;
    void Snapshot() { }
    void BeginInvoke(Action action) => action();
    Task InstallPackage(string path, string version, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); installs++; return Task.CompletedTask; }

    public static async Task CheckConsent(Action<bool, string> check)
    {
        var window = new MainWindow(); RemoteUpdates.Release = new("0.5.9", "https://example.invalid/setup.exe", 0);
        window.ScheduleUpdateCheck();
        check(window.remoteRelease?.Version == "0.5.9" && RemoteUpdates.Downloads == 0 && window.installs == 0,
            "startup check offers update without downloading or installing");
        window.nextUpdateCheck = DateTimeOffset.MinValue; window.ScheduleUpdateCheck();
        check(RemoteUpdates.Downloads == 0 && window.installs == 0, "periodic check still requires consent");
        window.DeferUpdate("0.5.9");
        window.profile = JsonSerializer.Deserialize<Profile>(Store.Saved!)!;
        await window.CheckUpdates();
        var model = JsonSerializer.SerializeToElement(window.UpdateModel());
        check(model.GetProperty("dismissedVersion").GetString() == "0.5.9" && model.GetProperty("availableVersion").GetString() == "0.5.9",
            "deferred version survives reload and remains available for manual update");
        RemoteUpdates.Release = new("0.5.10", "https://example.invalid/setup.exe", 0); await window.CheckUpdates();
        bool stale = false; try { await window.DownloadUpdate("0.5.9"); } catch (UserError) { stale = true; }
        check(stale && RemoteUpdates.Downloads == 0 && window.installs == 0, "stale consent cannot install a different version");
        bool absent = false; try { await window.DownloadUpdate(""); } catch (UserError) { absent = true; }
        check(absent && RemoteUpdates.Downloads == 0, "missing version consent rejected before download");
        RemoteUpdates.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var download = window.DownloadUpdate("0.5.10");
        bool duplicate = false; try { await window.DownloadUpdate("0.5.10"); } catch (UserError) { duplicate = true; }
        check(duplicate && RemoteUpdates.Downloads == 1 && window.installs == 0, "duplicate consent cannot start another download");
        window.updateAttempt!.Cancel(); await download; RemoteUpdates.Hold = null;
        check(window.installs == 0 && !window.updateBusy && !Directory.EnumerateFiles(Store.Folder).Any(),
            "cancellation keeps running app and removes temporary download");
        await window.DownloadUpdate("0.5.10");
        check(RemoteUpdates.Downloads == 2 && window.installs == 1 && !Directory.EnumerateFiles(Store.Folder).Any(),
            "explicit consent downloads and installs exactly once");
        window.lifetime.Dispose(); window.operations.Dispose();
    }
}

internal static class RemoteUpdates
{
    internal static UpdateRelease? Release;
    internal static int Downloads;
    internal static TaskCompletionSource? Hold;
    internal static Task<UpdateRelease?> Check(Version current, CancellationToken ct) => Task.FromResult(Release);
    internal static async Task Download(UpdateRelease release, Stream output, CancellationToken ct, Action<long> progress)
    { Downloads++; if (Hold != null) await Hold.Task.WaitAsync(ct); ct.ThrowIfCancellationRequested(); }
}
internal static class Store
{
    internal static string Folder => Path.Combine("Tests", "generated", "consent");
    internal static string? Saved;
    internal static void Save(Profile profile) => Saved = JsonSerializer.Serialize(profile);
    internal static string Friendly(Exception ex) => ex.Message;
}
internal static class AppLog
{
    internal static void Write(string category, string text) { }
    internal static void Error(string category, Exception ex) { }
}
