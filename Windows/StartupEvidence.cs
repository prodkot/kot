using System.Diagnostics;
using System.Text.Json;
using Kot.Core;
namespace Kot.Windows;

// One bounded record for the last --startup launch. Never stores settings or paths.
internal static class StartupEvidence
{
    static bool active, ready, tray;
    static DateTimeOffset started;
    public static void Begin() { active = true; started = DateTimeOffset.UtcNow; Write("starting"); }
    public static void Ready(bool trayVisible) { ready = true; tray = trayVisible; Write("ready"); }
    public static void Write(string stage)
    {
        if (!active) return;
        try
        {
            Directory.CreateDirectory(Store.Folder);
            using var process = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(Store.Folder, "startup-lifecycle.json"), JsonSerializer.Serialize(new {
                version = ClientIdentity.Version, buildNumber = ClientIdentity.BuildNumber, pid = Environment.ProcessId, session = process.SessionId,
                startedUtc = started, updatedUtc = DateTimeOffset.UtcNow, stage, uiReady = ready, trayVisible = tray
            }));
        }
        catch { /* Diagnostics must never stop startup. */ }
    }
}
