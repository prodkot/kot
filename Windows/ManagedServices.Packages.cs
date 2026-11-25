using Kot.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Kot.Windows;

internal sealed partial class ManagedServices
{
    string Root(string id) => Path.Combine(folder, id);
    string Manifest(string id) => Path.Combine(Root(id), "current.json");
    void PrepareIntegrity(Entry entry)
    {
        if (ServiceIntegrity.Prepare(folder, Root(entry.Id), entry.Id == "zapret" ? ServiceIntegrity.High : ServiceIntegrity.Medium))
            AddLog(entry, "Восстановлены метки доступа файлов сервиса для корректного запуска.");
    }
    string Exe(Entry entry) => OwnedPath(entry.Id, entry.Installed?.Executable ?? throw new UserError("Сначала установите сервис."));
    string OwnedPath(string id, string relative)
    {
        string root = Path.GetFullPath(Root(id)) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !path.StartsWith(root + "packages" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UserError("Некорректная папка управляемого сервиса.");
        for (string? p = path; p != null && p.Length >= root.Length; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new UserError("Папка сервиса содержит перенаправление.");
        return path;
    }
    ServiceInstallation? ReadInstallation(string id)
    {
        string path = Manifest(id); if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8192) throw new UserError("Некорректный манифест сервиса.");
        var value = JsonSerializer.Deserialize<ServiceInstallation>(File.ReadAllText(path), json) ?? throw new UserError("Не прочитан манифест сервиса.");
        if (!ServicePackages.ValidVersion(value.Version) || value.Sha256.Length != 64 || !value.Sha256.All(Uri.IsHexDigit) || Path.GetFileName(value.Executable) != (id == "zapret" ? "winws.exe" : "TgWsProxy_windows.exe") || !File.Exists(OwnedPath(id, value.Executable))) throw new UserError("Файлы установленного сервиса не найдены.");
        return value;
    }
    static void Atomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temp, data); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    void Commit(Entry entry, ServiceInstallation install) => Atomic(Manifest(entry.Id), JsonSerializer.SerializeToUtf8Bytes(install, json));
    async Task Check(Entry entry, CancellationToken ct)
    {
        entry.Latest = await ServicePackages.Latest(entry.Id, ct, transport: metadataTransport?.Invoke(), proxyPort: proxyPort()); entry.Checked = DateTimeOffset.UtcNow;
        AddLog(entry, "Последняя стабильная версия: " + entry.Latest.Version);
    }
    async Task Install(Entry entry, CancellationToken ct)
    {
        await Check(entry, ct); var release = entry.Latest!;
        if (entry.Installed != null && !ServicePackages.Newer(release.Version, entry.Installed.Version)) { AddLog(entry, "Установлена актуальная версия."); return; }
        string root = Root(entry.Id); Directory.CreateDirectory(root);
        string staging = Path.Combine(root, "staging-" + Guid.NewGuid().ToString("N")), download = Path.Combine(root, "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string? package = null; var old = entry.Installed; bool running = entry.Process != null && !entry.Process.HasExited, committed = false, applying = false;
        try
        {
            await ServicePackages.Download(release, download, new Progress<double>(p => { entry.Progress = p; }), ct, proxyPort: proxyPort());
            entry.Phase = "Проверка и подготовка"; changed();
            string executable;
            if (entry.Id == "zapret")
            {
                await Task.Run(() => ServicePackages.Extract(download, staging, ct), ct);
                var candidates = Directory.GetFiles(staging, "winws.exe", SearchOption.AllDirectories);
                if (candidates.Length != 1 || Path.GetFileName(Path.GetDirectoryName(candidates[0])) != "bin") throw new UserError("В пакете отсутствует winws.exe.");
                executable = Path.GetRelativePath(staging, candidates[0]);
                string releaseRoot = Path.GetDirectoryName(Path.GetDirectoryName(candidates[0]))!;
                PrepareLists(releaseRoot);
                var strategies = Directory.GetFiles(releaseRoot, "*.bat").Where(s => ZapretStrategy.ValidName(Path.GetFileName(s))).ToArray();
                if (strategies.Length == 0) throw new UserError("В пакете отсутствуют стратегии zapret.");
                foreach (var strategy in strategies) ZapretStrategy.Parse(File.ReadAllText(strategy), Path.Combine(releaseRoot, "bin"), Path.Combine(releaseRoot, "lists"), options().Zapret.GameFilter);
                if (!strategies.Any(s => Path.GetFileName(s) == options().Zapret.Strategy)) throw new UserError("Сохранённая стратегия отсутствует в обновлении. Выберите general.bat перед обновлением.");
            }
            else
            {
                executable = "TgWsProxy_windows.exe";
                await using var file = File.OpenRead(download); byte[] signature = new byte[2]; if (await file.ReadAsync(signature, ct) != 2 || signature[0] != 'M' || signature[1] != 'Z') throw new UserError("Пакет Telegram не является Windows-приложением.");
                File.Copy(download, Path.Combine(staging, executable));
                File.WriteAllText(Path.Combine(staging, "LICENSE.txt"), ServicePackages.TelegramLicense);
            }
            ct.ThrowIfCancellationRequested();
            string relative = Path.Combine("packages", release.Version + "-" + Guid.NewGuid().ToString("N")); package = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(package)!);
            Directory.Move(staging, package); var install = new ServiceInstallation(release.Version, Path.Combine(relative, executable), release.Sha256);
            PrepareIntegrity(entry);
            entry.Phase = "Применение обновления"; applying = true; await Stop(entry); ct.ThrowIfCancellationRequested();
            entry.Installed = install;
            if (running) await Start(entry, null, ct);
            Commit(entry, install); committed = true; AddLog(entry, "Установлена версия " + release.Version); entry.Status = entry.Process == null ? "Остановлен" : "Работает";
            // Retain the previous package for recovery; remove older managed packages.
            string? keep = old == null ? null : PackageDirectory(entry.Id, old.Executable);
            foreach (string directory in Directory.GetDirectories(Path.Combine(root, "packages")))
                if (directory != package && directory != keep)
                {
                    try { if (ManagedPackageFiles.Remove(root, directory)) AddLog(entry, "Занятые файлы старого пакета будут удалены после перезагрузки Windows."); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or UserError) { AddLog(entry, "Не удалось очистить старый пакет: " + Store.Friendly(ex)); }
                }
        }
        catch
        {
            if (!committed && applying)
            {
                await Stop(entry); entry.Installed = old;
                if (running && old != null && !shuttingDown) { try { await Start(entry, null, CancellationToken.None); } catch (Exception ex) { AddLog(entry, "Не удалось перезапустить предыдущую версию: " + Store.Friendly(ex)); } }
            }
            throw;
        }
        finally
        {
            TryDelete(download); ManagedPackageFiles.Remove(root, staging);
            if (!committed && package != null) ManagedPackageFiles.Remove(root, package);
        }
    }
    string PackageDirectory(string id, string executable)
    {
        string path = OwnedPath(id, executable); string packages = Path.Combine(Path.GetFullPath(Root(id)), "packages");
        while (Path.GetDirectoryName(path) != packages) path = Path.GetDirectoryName(path) ?? throw new UserError("Некорректная папка пакета.");
        return path;
    }
}
