using System.Diagnostics;
using System.Text.Json;
namespace Kot.Core;

// Startup can fail before a profile/redactor is loaded. Never persist exception
// messages, paths, URLs or profile data in this diagnostic record.
public static class StartupDiagnostics
{
    public static string Capture(string stage, Exception error) => JsonSerializer.Serialize(new
    {
        version = ClientIdentity.Version,
        stage = stage == "interface" ? "interface" : "startup",
        utc = DateTimeOffset.UtcNow,
        exceptions = Chain(error).Select(e => new
        {
            type = e.GetType().FullName,
            hresult = e.HResult,
            frames = new StackTrace(e, false).GetFrames().Take(12)
                .Select(f => f.GetMethod()).Where(m => m?.DeclaringType?.Namespace?.StartsWith("Kot.", StringComparison.Ordinal) == true)
                .Select(m => m!.DeclaringType!.FullName + "." + m.Name).ToArray()
        }).ToArray()
    });
    static IEnumerable<Exception> Chain(Exception error)
    {
        for (int i = 0; error != null && i < 4; i++, error = error.InnerException!) yield return error;
    }
}
