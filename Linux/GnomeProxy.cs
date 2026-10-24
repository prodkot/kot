using System.Text.Json;
using Kot.Core;
namespace Kot.Linux;

public sealed class GnomeProxy
{
    public sealed record Receipt(Dictionary<string, string> Original, Dictionary<string, string> Applied);
    readonly string path = Path.Combine(Paths.Config, "gnome-proxy.json");
    readonly Func<string[], Task<string>> run;
    public GnomeProxy(Func<string[], Task<string>>? transport = null) => run = transport ?? (args => OwnedProcess.Run("/usr/bin/gsettings", args));
    static string[] Key(string name) => name.Split('/', 2);
    async Task<string> Read(string name) { var key = Key(name); return await run(["get", key[0], key[1]]); }
    async Task Write(string name, string value) { var key = Key(name); await run(["set", key[0], key[1], value]); }
    public async Task Enable(int port)
    {
        if (port is < 1024 or > 65535) throw new UserError("Неверный порт прокси.");
        await Restore();
        var applied = new Dictionary<string, string>
        {
            ["org.gnome.system.proxy.http/host"] = "'127.0.0.1'", ["org.gnome.system.proxy.http/port"] = port.ToString(),
            ["org.gnome.system.proxy.https/host"] = "'127.0.0.1'", ["org.gnome.system.proxy.https/port"] = port.ToString(),
            ["org.gnome.system.proxy.http/use-authentication"] = "false",
            ["org.gnome.system.proxy/use-same-proxy"] = "false",
            ["org.gnome.system.proxy.socks/host"] = "''", ["org.gnome.system.proxy.socks/port"] = "0",
            ["org.gnome.system.proxy/ignore-hosts"] = "['localhost', '127.0.0.0/8', '::1']",
            ["org.gnome.system.proxy/mode"] = "'manual'"
        };
        Dictionary<string, string> original = [];
        foreach (string key in applied.Keys) original[key] = await Read(key);
        Paths.Atomic(path, JsonSerializer.SerializeToUtf8Bytes(new Receipt(original, applied)));
        try { foreach (var (key, value) in applied) { await Write(key, value); if (await Read(key) != value) throw new UserError("GNOME не применил настройку прокси: " + key); } }
        catch { await Restore(); throw; }
    }
    public async Task Restore()
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 16384) throw new UserError("Повреждён журнал прокси; требуется ручное восстановление.");
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllBytes(path)) ?? throw new UserError("Повреждён журнал прокси.");
        // Validate the whole receipt before the first write. A corrupt late key
        // must not leave an already partially restored proxy configuration.
        if (receipt.Original == null || receipt.Applied == null || receipt.Applied.Count == 0
            || !receipt.Original.Keys.ToHashSet().SetEquals(receipt.Applied.Keys)
            || receipt.Applied.Any(pair => !Allowed(pair.Key) || pair.Value == null || receipt.Original[pair.Key] == null))
            throw new UserError("Неверный журнал прокси; настройки не изменены.");
        // Restore each key only if unchanged. A user's intervening change wins.
        foreach (var (key, value) in receipt.Applied.Reverse())
        {
            if (await Read(key) == value) await Write(key, receipt.Original[key]);
        }
        File.Delete(path);
    }
    static bool Allowed(string key) => key is "org.gnome.system.proxy.http/host" or "org.gnome.system.proxy.http/port" or "org.gnome.system.proxy.https/host" or "org.gnome.system.proxy.https/port" or "org.gnome.system.proxy.http/use-authentication" or "org.gnome.system.proxy/ignore-hosts" or "org.gnome.system.proxy/mode" or "org.gnome.system.proxy/use-same-proxy" or "org.gnome.system.proxy.socks/host" or "org.gnome.system.proxy.socks/port";
}
