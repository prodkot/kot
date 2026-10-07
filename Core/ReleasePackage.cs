using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
namespace Kot.Core;
public sealed record ReleaseManifest(string Version, Dictionary<string, string> Files);
public static class ReleasePackage
{
    public static ReleaseManifest Validate(ZipArchive archive, Version? current = null, string? publicKey = null)
    {
        if (archive.Entries.Count is < 3 or > 3000 || archive.Entries.Sum(e => e.Length) > 600L * 1024 * 1024) throw new UserError("Недопустимый размер обновления.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!SafeName(entry.FullName) || !entries.TryAdd(entry.FullName, entry) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new UserError("Небезопасные пути в архиве обновления.");
        }
        foreach (string name in entries.Keys)
        {
            var parts = name.Split('/'); string parent = "";
            for (int i = 0; i < parts.Length - 1; i++) { parent = parent.Length == 0 ? parts[i] : parent + "/" + parts[i]; if (entries.ContainsKey(parent)) throw new UserError("Файл пересекается с папкой в архиве обновления."); }
        }
        byte[] Read(string name, int max)
        {
            if (!entries.TryGetValue(name, out var entry) || entry.Length > max) throw new UserError("Нет подписанного манифеста обновления.");
            using var input = entry.Open(); using var data = new MemoryStream();
            byte[] block = new byte[8192]; int count;
            while ((count = input.Read(block)) > 0)
            {
                if (data.Length + count > max || data.Length + count > entry.Length) throw new UserError("Недопустимый размер манифеста обновления.");
                data.Write(block, 0, count);
            }
            if (data.Length != entry.Length) throw new UserError("Повреждённый манифест обновления.");
            return data.ToArray();
        }
        byte[] manifestData = Read("release.json", 1024 * 1024), signature = Read("release.sig", 4096);
        using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey ?? ReleaseKey.Public);
        if (!rsa.VerifyData(manifestData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new UserError("Подпись обновления не прошла проверку.");
        ReleaseManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestData, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new UserError("Пустой манифест."); }
        catch (JsonException) { throw new UserError("Некорректный манифест обновления."); }
        if (!Version.TryParse(manifest.Version, out var version) || (current != null && version <= current)) throw new UserError("В архиве нет более новой версии kot.");
        if (manifest.Files == null || !manifest.Files.ContainsKey("Kot.exe") || !manifest.Files.ContainsKey("core/sing-box.exe") || entries.Count != manifest.Files.Count + 2)
            throw new UserError("Неполная сборка обновления.");
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Files)
        {
            if (!SafeName(item.Key) || item.Key is "release.json" or "release.sig" || !unique.Add(item.Key) || !entries.TryGetValue(item.Key, out var entry) || item.Value == null || item.Value.Length != 64)
                throw new UserError("Некорректный список файлов обновления.");
            using var stream = entry.Open(); using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] block = new byte[81920]; long size = 0; int count;
            while ((count = stream.Read(block)) > 0)
            {
                size += count;
                if (size > entry.Length) throw new UserError("Недопустимый размер файла обновления.");
                digest.AppendData(block, 0, count);
            }
            if (size != entry.Length) throw new UserError("Повреждённый файл обновления.");
            string hash = Convert.ToHexString(digest.GetHashAndReset());
            if (!hash.Equals(item.Value, StringComparison.OrdinalIgnoreCase)) throw new UserError("Повреждённый файл обновления: " + item.Key);
        }
        return manifest;
    }
    public static bool SafeName(string name)
    {
        if (name.Length is < 1 or > 240 || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.EndsWith('/')) return false;
        foreach (string part in name.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<','>','"','|','?','*','\0']) >= 0) return false;
            if (part.Any(char.IsControl)) return false;
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9') return false;
        }
        return true;
    }
}
