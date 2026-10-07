using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Kot.Core;
public sealed class UpdateOptions
{
    public string Source { get; set; } = "github";
    public string Address { get; set; } = "";
    public bool Automatic { get; set; } = true;
    public void Validate()
    {
        if (Source is not ("github" or "https") || Address == null || Address.Length > 2048) throw new UserError("Некорректный источник обновлений.");
        if (Address.Length == 0) return;
        if (Source == "github")
        {
            if (!Regex.IsMatch(Address, @"^[a-zA-Z0-9][a-zA-Z0-9-]{0,38}/[a-zA-Z0-9_.-]{1,100}$") || Address.EndsWith("/.") || Address.EndsWith("/..")) throw new UserError("Укажите GitHub-репозиторий: owner/repository.");
        }
        else RemoteUpdates.Https(Address);
    }
}
public sealed record UpdateRelease(string Version, string Url, long Size);
public static class RemoteUpdates
{
    public const long MaximumArchive = 600L * 1024 * 1024;
    public static Uri Https(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw new UserError("Для обновления нужна HTTPS-ссылка без логина и пароля.");
        return uri;
    }
    public static UpdateRelease? Parse(string json, string source, Version current)
    {
        if (json.Length > 1024 * 1024) throw new UserError("Слишком большой ответ сервера обновлений.");
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        string tag = root.GetProperty(source == "github" ? "tag_name" : "version").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version.Major < 0) throw new UserError("Сервер вернул некорректную версию.");
        if (version <= current) return null;
        string url; long size;
        if (source == "github")
        {
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() || root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;
            string expected = "Kot-Client-" + version + "-beta-Windows-x64.zip";
            var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == expected).ToArray();
            if (assets.Length != 1) throw new UserError("В релизе не найден архив kot. для Windows x64.");
            url = assets[0].GetProperty("browser_download_url").GetString() ?? ""; size = assets[0].GetProperty("size").GetInt64();
        }
        else { url = root.GetProperty("url").GetString() ?? ""; size = root.TryGetProperty("size", out var bytes) ? bytes.GetInt64() : 0; }
        Https(url); if (size < 0 || size > MaximumArchive) throw new UserError("Некорректный размер обновления.");
        return new(version.ToString(), url, size);
    }
    public static HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip }) { Timeout = Timeout.InfiniteTimeSpan };
    public static async Task<UpdateRelease?> Check(UpdateOptions options, Version current, CancellationToken ct, HttpClient? client = null)
    {
        options.Validate(); if (options.Address.Length == 0) throw new UserError("Сначала укажите источник обновлений.");
        bool owned = client == null; client ??= Client();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var data = new MemoryStream();
            await Fetch(client, options.Source == "github" ? "https://api.github.com/repos/" + options.Address + "/releases/latest" : options.Address, data, 1024 * 1024, deadline.Token);
            return Parse(System.Text.Encoding.UTF8.GetString(data.ToArray()), options.Source, current);
        }
        finally { if (owned) client.Dispose(); }
    }
    public static async Task Download(UpdateRelease release, Stream output, CancellationToken ct, Action<long>? progress = null, HttpClient? client = null)
    {
        bool owned = client == null; client ??= Client();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try { long received = await Fetch(client, release.Url, output, MaximumArchive, deadline.Token, progress); if (release.Size > 0 && received != release.Size) throw new UserError("Размер загруженного обновления не совпадает с релизом."); }
        finally { if (owned) client.Dispose(); }
    }
    static async Task<long> Fetch(HttpClient client, string url, Stream output, long limit, CancellationToken ct, Action<long>? progress = null)
    {
        Uri address = Https(url);
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("Kot-Client/" + ClientIdentity.Version); request.Headers.Accept.ParseAdd("application/json, application/octet-stream");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            { var location = response.Headers.Location ?? throw new UserError("Пустое перенаправление обновления."); address = Https(new Uri(address, location).AbsoluteUri); continue; }
            if (response.StatusCode == HttpStatusCode.NotFound) throw new UserError("Источник или опубликованный релиз не найден.");
            if ((int)response.StatusCode is 403 or 429) throw new UserError("GitHub ограничил запросы. Проверка будет повторена позже.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new UserError("Файл обновления слишком большой.");
            using var body = await response.Content.ReadAsStreamAsync(ct); byte[] buffer = new byte[65536]; long total = 0;
            while (true)
            {
                int read = await body.ReadAsync(buffer, ct); if (read == 0) break;
                total += read; if (total > limit) throw new UserError("Файл обновления слишком большой.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct); progress?.Invoke(total);
            }
            return total;
        }
        throw new UserError("Слишком много перенаправлений обновления.");
    }
}
