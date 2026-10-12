using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Diagnostics;
using System.Runtime.Versioning;
using Kot.Core;
namespace Kot.Windows;

// The portable build never falls back to the installed client's profile.
public static class AppRuntime
{
#if KOT_PORTABLE
    public static bool Portable => true;
#else
    public static bool Portable => false;
#endif
    public static string DisplayName => Portable ? "kot. portable" : "kot.";
    static readonly string FolderId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant())))[..16];
    public static string Scope => Portable ? "KotVPN-Portable-" + FolderId : "KotVPN";
    [SupportedOSPlatform("windows")]
    public static string InstanceMutex
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return "Local\\" + Scope + "-" + identity.User!.Value; }
    }
    public static string DataFolder => Portable ? Path.Combine(AppContext.BaseDirectory, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KotVPN");
    public static string BrowserFolder => Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
    public static string TunnelName => Portable ? "kot-pt-" + FolderId[..8].ToLowerInvariant() : "kot-tun";
    internal static Guid FilterKey(int number) => new(SHA256.HashData(Encoding.UTF8.GetBytes(Scope + ":wfp:" + number))[..16]);
    public static void Prepare()
    {
        if (!Portable) return;
        // Child cores, TgWsProxy bundles and Chromium inherit these local paths.
        foreach (string name in new[] { "", "temp", "bundles" })
        {
            string path = Path.Combine(DataFolder, name); Directory.CreateDirectory(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UserError("Portable-папка данных не должна содержать перенаправления.");
        }
        Environment.SetEnvironmentVariable("TEMP", Path.Combine(DataFolder, "temp"));
        Environment.SetEnvironmentVariable("TMP", Path.Combine(DataFolder, "temp"));
        Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", Path.Combine(DataFolder, "bundles"));
        Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", BrowserFolder);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(DataFolder, "WebView"));
        // Fixed Version 120+ renderers need these read permissions on Windows 10.
        if (OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build < 22000 && Directory.Exists(BrowserFolder) && !File.Exists(Path.Combine(DataFolder, "webview-acl-ready")))
        {
            foreach (string sid in new[] { "S-1-15-2-1", "S-1-15-2-2" })
            {
                var start = new ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(BrowserFolder); start.ArgumentList.Add("/grant"); start.ArgumentList.Add("*" + sid + ":(OI)(CI)(RX)");
                using var process = Process.Start(start) ?? throw new UserError("Не удалось подготовить права portable WebView2.");
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000)) { process.Kill(true); throw new UserError("Подготовка прав portable WebView2 не завершилась."); }
                Task.WhenAll(output, error).GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw new UserError("Нет доступа к WebView2Runtime. Распакуйте portable в собственную доступную для записи папку.");
            }
            File.WriteAllText(Path.Combine(DataFolder, "webview-acl-ready"), "ready");
        }
    }
    public static void Normalize(Profile profile) { if (Portable) profile.Startup = false; }
    public static void RequireInstalledUpdates()
    {
        if (Portable) throw new UserError("Portable обновляется распаковкой нового тестового архива. Установщик обычной версии здесь отключён.");
    }
}
