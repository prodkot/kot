using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Kot.Core;

public sealed class ServiceOptions
{
    public bool CheckUpdates { get; set; } = true;
    public ZapretOptions Zapret { get; set; } = new();
    public TelegramProxyOptions Telegram { get; set; } = new();
    public void Validate()
    {
        Zapret ??= new(); Telegram ??= new(); Zapret.Validate(); Telegram.Validate();
    }
}
public sealed class ZapretOptions
{
    public bool AutoStart { get; set; }
    public string Strategy { get; set; } = "general.bat";
    public bool GameFilter { get; set; }
    public string Domains { get; set; } = "";
    public string ExcludedDomains { get; set; } = "";
    public string Targets { get; set; } = "https://discord.com\nhttps://gateway.discord.gg\nhttps://www.youtube.com\nhttps://i.ytimg.com";
    public int TimeoutSeconds { get; set; } = 6;
    public void Validate()
    {
        Strategy ??= "general.bat"; Domains ??= ""; ExcludedDomains ??= ""; Targets ??= "";
        if (!ZapretStrategy.ValidName(Strategy) || TimeoutSeconds is < 2 or > 20 || Domains.Length > 16000 || ExcludedDomains.Length > 16000)
            throw new UserError("Некорректные настройки zapret.");
        Configuration.Domains(Domains); Configuration.Domains(ExcludedDomains); ServiceProbe.Targets(Targets);
    }
}
public sealed class TelegramProxyOptions
{
    public bool AutoStart { get; set; }
    public int Port { get; set; } = 1443;
    public string Secret { get; set; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    public bool Cloudflare { get; set; } = true;
    public bool Http2 { get; set; } = true;
    public int PoolSize { get; set; } = 4;
    public string DcIp { get; set; } = "2:149.154.167.220\n4:149.154.167.220";
    public string Domains { get; set; } = "";
    public string WorkerDomains { get; set; } = "";
    public void Validate()
    {
        DcIp ??= ""; Domains ??= ""; WorkerDomains ??= "";
        if (Port is < 1024 or > 65535 || PoolSize is < 0 or > 32 || Secret == null || !Regex.IsMatch(Secret, "^[0-9a-fA-F]{32}$"))
            throw new UserError("Порт Telegram должен быть от 1024 до 65535; secret должен содержать 32 шестнадцатеричных символа.");
        if (DcIp.Length > 2000 || Domains.Length > 4000 || WorkerDomains.Length > 4000) throw new UserError("Слишком большие настройки Telegram.");
        var entries = DcIp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length > 16) throw new UserError("Максимум 16 адресов DC.");
        foreach (string line in entries)
        {
            int colon = line.IndexOf(':');
            if (colon < 1 || !int.TryParse(line[..colon], out int dc) || dc is < 1 or > 5 || !IPAddress.TryParse(line[(colon + 1)..], out var ip) || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                throw new UserError("Адрес DC задаётся как 2:149.154.167.220.");
        }
        Configuration.Domains(Domains); Configuration.Domains(WorkerDomains);
    }
    public string Link { get { Validate(); return $"tg://proxy?server=127.0.0.1&port={Port}&secret=dd{Secret.ToLowerInvariant()}"; } }
    // The official Windows binary has a tray entry point, not the Python CLI.
    public byte[] ServiceConfig()
    {
        Validate();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            host = "127.0.0.1", port = Port, secret = Secret.ToLowerInvariant(),
            dc_ip = DcIp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            cfproxy = Cloudflare, h2 = Http2, pool_size = PoolSize, buf_kb = 256,
            cfproxy_user_domain_enabled = Domains.Trim().Length > 0, cfproxy_user_domain = Configuration.Domains(Domains),
            cfproxy_worker_enabled = WorkerDomains.Trim().Length > 0, cfproxy_worker_domain = Configuration.Domains(WorkerDomains),
            check_updates = false, autostart = false, verbose = false, log_max_mb = 2, language = "ru",
            no_secure = false, force_test_dc = false, appearance = "auto"
        });
    }
}
public static class WindowsArguments
{
    // CommandLineToArgvW quoting, including trailing backslashes and literal quotes.
    public static string Quote(string value)
    {
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new UserError("Недопустимый аргумент процесса.");
        var result = new StringBuilder("\""); int slash = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slash++; continue; }
            result.Append('\\', c == '"' ? slash * 2 + 1 : slash).Append(c); slash = 0;
        }
        return result.Append('\\', slash * 2).Append('"').ToString();
    }
    public static string Command(string exe, IEnumerable<string> args) => string.Join(' ', new[] { Quote(exe) }.Concat(args.Select(Quote)));
}
public static class ZapretStrategy
{
    public static bool ValidName(string name) => name.Length <= 120 && Regex.IsMatch(name, @"^general(?:[ ()A-Za-z0-9_.+-]*)\.bat$", RegexOptions.IgnoreCase) && !name.Contains("..");
    public static string[] Parse(string text, string bin, string lists, bool games)
    {
        if (text.Length > 64000) throw new UserError("Стратегия zapret слишком большая.");
        const string marker = "\"%BIN%winws.exe\"";
        int start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || text.IndexOf(marker, start + marker.Length, StringComparison.OrdinalIgnoreCase) >= 0) throw new UserError("Не найден единственный запуск winws.exe в стратегии.");
        string command = text[(start + marker.Length)..].Trim();
        command = Regex.Replace(command, @"\^\s*\r?\n", " ");
        // Read only the launch instruction; never execute the BAT preamble or tail.
        int end = command.IndexOfAny(['\r', '\n']); if (end >= 0) command = command[..end];
        // The official FAKE TLS AUTO strategy escapes winws's literal ! for CMD.
        command = command.Replace("^!", "!", StringComparison.Ordinal);
        command = Regex.Replace(command, "%GameFilter(?:TCP|UDP)?%", _ => games ? "1024-65535" : "12", RegexOptions.IgnoreCase);
        string template = Regex.Replace(command, "%BIN%|%LISTS%", "", RegexOptions.IgnoreCase);
        if (template.IndexOfAny(['%', '^', '&', '|', '<', '>', '\0', '`']) >= 0) throw new UserError("Неподдерживаемая команда в стратегии zapret.");
        command = Regex.Replace(command, "%BIN%", _ => Path.GetFullPath(bin).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, RegexOptions.IgnoreCase);
        command = Regex.Replace(command, "%LISTS%", _ => Path.GetFullPath(lists).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, RegexOptions.IgnoreCase);
        var args = new List<string>(); var token = new StringBuilder(); bool quote = false;
        foreach (char c in command)
        {
            if (c == '"') { quote = !quote; continue; }
            if (char.IsWhiteSpace(c) && !quote) { if (token.Length > 0) { args.Add(token.ToString()); token.Clear(); } }
            else token.Append(c);
        }
        if (quote) throw new UserError("Незакрытая кавычка в стратегии zapret.");
        if (token.Length > 0) args.Add(token.ToString());
        if (args.Count is < 2 or > 400 || args.Any(a => !a.StartsWith("--", StringComparison.Ordinal) || a.Length > 4096)) throw new UserError("Некорректные параметры стратегии zapret.");
        foreach (string arg in args)
        {
            string value = arg[(arg.IndexOf('=') + 1)..].TrimStart('@');
            // File arguments from release strategies must stay inside bin/lists.
            if (value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar) || Regex.IsMatch(value, @"^[A-Za-z]:"))
            {
                if (!Within(value, bin) && !Within(value, lists) || !File.Exists(value)) throw new UserError("Файл стратегии выходит за папку пакета или отсутствует.");
            }
        }
        return args.ToArray();
    }
    static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
public sealed record ServiceProbeResult(string Target, bool Success, int Milliseconds, int? Status, string Error);
public sealed record StrategyResult(string Strategy, List<ServiceProbeResult> Checks, string Error = "")
{
    public int Passed => Checks.Count(c => c.Success);
    public double Latency => Checks.Where(c => c.Success).Select(c => (double)c.Milliseconds).DefaultIfEmpty(double.MaxValue).Average();
}
public sealed class StrategyTestRun
{
    public DateTimeOffset Started { get; set; } = DateTimeOffset.UtcNow;
    public string Version { get; set; } = "";
    public bool Cancelled { get; set; }
    public List<ServiceProbeResult> Baseline { get; set; } = [];
    public List<StrategyResult> Results { get; set; } = [];
    public string? Recommended => Results.Where(r => r.Error.Length == 0 && r.Passed > Baseline.Count(c => c.Success))
        .OrderByDescending(r => r.Passed).ThenBy(r => r.Latency).Select(r => r.Strategy).FirstOrDefault();
}
public static class ServiceProbe
{
    public static Uri[] Targets(string value)
    {
        if (value.Length > 4000) throw new UserError("Список адресов для теста слишком большой.");
        var lines = value.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
        if (lines.Length is < 1 or > 12) throw new UserError("Для проверки нужно от 1 до 12 HTTPS-адресов.");
        return lines.Select(s =>
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length > 0 || url.Fragment.Length > 0 || url.Port != 443 || Uri.CheckHostName(url.Host) != UriHostNameType.Dns || url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                throw new UserError("Для теста используйте публичные HTTPS-адреса без логина и нестандартного порта.");
            return url;
        }).ToArray();
    }
    public static async Task<List<ServiceProbeResult>> Run(Uri[] targets, int seconds, CancellationToken cancellation)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(seconds), PooledConnectionLifetime = TimeSpan.Zero };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(seconds) };
        using var concurrency = new SemaphoreSlim(3);
        return (await Task.WhenAll(targets.Select(async target =>
        {
            await concurrency.WaitAsync(cancellation); var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                request.Headers.UserAgent.ParseAdd("kot-service-test/" + ClientIdentity.Version);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
                int code = (int)response.StatusCode;
                // A valid TLS/HTTP answer (including 403/404 at CDN roots) proves reachability, not playback/voice.
                bool reachable = code is >= 200 and < 500 && code != 429;
                return new ServiceProbeResult(target.AbsoluteUri, reachable, (int)clock.ElapsedMilliseconds, code, reachable ? "" : "HTTP " + code);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new ServiceProbeResult(target.AbsoluteUri, false, (int)clock.ElapsedMilliseconds, null, "Время ожидания истекло"); }
            catch (HttpRequestException) { return new ServiceProbeResult(target.AbsoluteUri, false, (int)clock.ElapsedMilliseconds, null, "Ошибка DNS, TCP или TLS"); }
            finally { concurrency.Release(); }
        }))).ToList();
    }
}
