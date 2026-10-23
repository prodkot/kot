using System.Runtime.InteropServices;
using Kot.Core;
namespace Kot.Linux;

public static class Paths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Config => Root("XDG_CONFIG_HOME", ".config");
    public static string Data => Root("XDG_DATA_HOME", ".local/share");
    public static string Cache => Root("XDG_CACHE_HOME", ".cache");
    public static string Runtime => Private(Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? throw new UserError("Нет XDG_RUNTIME_DIR. Запустите kot. в пользовательском сеансе GNOME."), "kot"));
    static string Root(string variable, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(variable) ?? Path.Combine(Home, fallback);
        if (!Path.IsPathFullyQualified(value)) throw new UserError("XDG-путь должен быть абсолютным.");
        return Private(Path.Combine(value, "kot"));
    }
    public static string Private(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            if (new DirectoryInfo(path).LinkTarget != null) throw new UserError("Каталог kot. не должен быть символьной ссылкой.");
        }
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
    public static void Atomic(string path, byte[] value)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            { stream.Write(value); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string Core => Path.Combine(AppContext.BaseDirectory, "core/xray");
    public static string Shell => Path.Combine(AppContext.BaseDirectory, "shell.py");
}
