using System.Security.Principal;
using System.Runtime.Versioning;
namespace Kot.Windows;

// Keep the installed client's identity and data paths stable across upgrades.
public static class AppRuntime
{
    public const string DisplayName = "kot.";
    public const string Scope = "KotVPN";
    [SupportedOSPlatform("windows")]
    public static string InstanceMutex
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return "Local\\" + Scope + "-" + identity.User!.Value; }
    }
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KotVPN");
}
