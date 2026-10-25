using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Kot.Core;

public enum ServicePlatform { Windows, Linux }
public sealed record ServiceRelease(string Id, string Version, Uri Download, long Size, string Sha256, Uri Page, ServicePlatform Platform = ServicePlatform.Windows);
public static class ServicePackages
{
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        uint crc = (uint)value;
        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        return crc;
    }).ToArray();
    public static string TelegramLicense
    {
        get { using var reader = new StreamReader(typeof(ServicePackages).Assembly.GetManifestResourceStream("Kot.Core.TelegramProxyLicense.txt")!); return reader.ReadToEnd(); }
    }

    public const long MaxDownload = 64 * 1024 * 1024;
    public static string Repository(string id) => id switch { "zapret" => "Flowseal/zapret-discord-youtube", "telegram" => "Flowseal/tg-ws-proxy", _ => throw new UserError("Неизвестный сервис.") };
    public static string Page(string id) => "https://github.com/" + Repository(id);
    public static bool ValidVersion(string version) => Regex.IsMatch(version, @"^v?\d{1,4}\.\d{1,4}\.\d{1,4}$");
    public static bool Newer(string latest, string installed) => ValidVersion(latest) && (!ValidVersion(installed) || Version.Parse(latest.TrimStart('v')) > Version.Parse(installed.TrimStart('v')));
    static string AssetName(string id, string version, ServicePlatform platform) => platform == ServicePlatform.Linux
        ? id == "telegram" ? "TgWsProxy_linux_amd64" : throw new UserError("Для Linux zapret нужен отдельный nfqws/tpws-пакет и Linux-стратегии; Windows BAT не используются.")
        : id == "zapret" ? $"zapret-discord-youtube-{version.TrimStart('v')}.zip" : "TgWsProxy_windows.exe";
    public static ServiceRelease Parse(string id, string json, ServicePlatform platform = ServicePlatform.Windows)
    {
        string repository = Repository(id);
        using var document = JsonDocument.Parse(json); var release = document.RootElement;
        string version = release.GetProperty("tag_name").GetString() ?? "";
        if (!ValidVersion(version) || release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) throw new UserError("Не найден стабильный выпуск сервиса.");
        string name = AssetName(id, version, platform);
        var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1) throw new UserError("В выпуске нет подходящей сборки для выбранной платформы.");
        var asset = assets[0]; string digest = asset.GetProperty("digest").GetString() ?? "";
        long size = asset.GetProperty("size").GetInt64();
        var download = new Uri(asset.GetProperty("browser_download_url").GetString() ?? "");
        var expected = new Uri($"https://github.com/{repository}/releases/download/{version}/{name}");
        if (!download.AbsoluteUri.Equals(expected.AbsoluteUri, StringComparison.OrdinalIgnoreCase) || download.UserInfo.Length > 0 || size is <= 0 or > MaxDownload || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$"))
            throw new UserError("Выпуск не содержит проверяемый официальный пакет и SHA-256.");
        return new(id, version, download, size, digest[7..].ToLowerInvariant(), new Uri($"https://github.com/{repository}/releases/tag/{version}"), platform);
    }
    public static async Task<ServiceRelease> Latest(string id, CancellationToken ct, HttpMessageHandler? transport = null, int proxyPort = 0, ServicePlatform platform = ServicePlatform.Windows, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(90)); ct = deadline.Token;
        using var http = Client(transport, proxyPort);
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository(id)}/releases/latest", HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429) throw new UserError("GitHub временно ограничил проверки. Попробуйте позже.");
        response.EnsureSuccessStatusCode();
        var bytes = await ReadLimited(response.Content, 1024 * 1024, ct);
        return Parse(id, System.Text.Encoding.UTF8.GetString(bytes), platform);
    }
    static HttpClient Client(HttpMessageHandler? handler, int proxyPort) { var client = new HttpClient(handler ?? NetworkRequests.Handler(proxyPort)) { Timeout = Timeout.InfiniteTimeSpan }; client.DefaultRequestHeaders.UserAgent.ParseAdd("kot-services/" + ClientIdentity.Version); return client; }
    public static async Task Download(ServiceRelease release, string path, IProgress<double>? progress, CancellationToken ct, HttpMessageHandler? transport = null, int proxyPort = 0, TimeSpan? timeout = null)
    {
        // Revalidate the descriptor even when it came from a local test or manifest.
        if (!ValidVersion(release.Version) || release.Download.AbsoluteUri != $"https://github.com/{Repository(release.Id)}/releases/download/{release.Version}/{AssetName(release.Id, release.Version, release.Platform)}" || release.Size is <= 0 or > MaxDownload || !Regex.IsMatch(release.Sha256, "^[a-f0-9]{64}$")) throw new UserError("Некорректный пакет сервиса.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(90)); ct = deadline.Token;
        using var http = Client(transport, proxyPort); Uri url = release.Download; bool created = false;
        try
        {
            for (int redirects = 0; redirects <= 4; redirects++)
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (response.Headers.Location == null || redirects == 4) throw new UserError("Слишком много перенаправлений загрузки.");
                    var next = new Uri(url, response.Headers.Location);
                    if (next.Scheme != "https" || next.UserInfo.Length > 0 || next.Port != 443 || next.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new UserError("Загрузка перенаправлена за пределы GitHub.");
                    url = next; continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw new UserError("Размер пакета не совпал с выпуском.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                created = true;
                byte[] buffer = new byte[65536]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += read; if (total > release.Size) throw new UserError("Пакет превысил заявленный размер.");
                    hash.AppendData(buffer.AsSpan(0, read)); await output.WriteAsync(buffer.AsMemory(0, read), ct); progress?.Report(total / (double)release.Size);
                }
                if (total != release.Size || !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.Ordinal)) throw new UserError("SHA-256 пакета не совпал. Установленная версия сохранена.");
                return;
            }
        }
        catch { if (created) { try { File.Delete(path); } catch { } } throw; }
        throw new UserError("Пакет не загружен.");
    }
    static async Task<byte[]> ReadLimited(HttpContent content, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream(); await using var input = await content.ReadAsStreamAsync(ct); byte[] buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0) { if (output.Length + read > limit) throw new UserError("Ответ сервиса слишком большой."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    public static void Extract(string archive, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination); string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive); long total = 0; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (zip.Entries.Count > 2000) throw new UserError("Слишком много файлов в пакете.");
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested(); string name = entry.FullName.Replace('\\', '/');
            string[] segments = name.TrimEnd('/').Split('/');
            if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.Any(char.IsControl) || s.IndexOfAny([':', '\0', '?', '*', '<', '>', '|', '"']) >= 0 || s.EndsWith('.') || s.EndsWith(' ') || Regex.IsMatch(s, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) || name.StartsWith('/') || !seen.Add(name.TrimEnd('/')) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new UserError("Небезопасный путь в пакете сервиса.");
            string target = Path.GetFullPath(Path.Combine(destination, name)); if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UserError("Пакет выходит за папку установки.");
            total += entry.Length; if (total > 128 * 1024 * 1024 || entry.Length > MaxDownload) throw new UserError("Распакованный пакет слишком большой.");
            if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            bool created = false;
            try
            {
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true; byte[] buffer = new byte[65536]; long written = 0; int read; uint crc = uint.MaxValue;
                while ((read = input.Read(buffer)) > 0)
                {
                    ct.ThrowIfCancellationRequested(); written += read;
                    if (written > entry.Length || written > MaxDownload) throw new UserError("Файл пакета превысил заявленный размер.");
                    for (int i = 0; i < read; i++) crc = (crc >> 8) ^ CrcTable[(crc ^ buffer[i]) & 255];
                    output.Write(buffer, 0, read);
                }
                if (written != entry.Length) throw new UserError("Файл пакета распакован не полностью.");
                if (~crc != entry.Crc32) throw new UserError("Контрольная сумма файла пакета не совпала.");
            }
            catch { if (created) File.Delete(target); throw; }
        }
    }
}
