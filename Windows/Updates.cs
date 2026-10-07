using Kot.Core;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
namespace Kot.Windows;
public sealed partial class MainWindow
{
    async Task InstallUpdate()
    {
        if (updateBusy) throw new UserError("Обновление уже выполняется.");
        using var choose = new OpenFileDialog { Filter = "Обновление kot. (*.zip)|*.zip", Title = "Выберите подписанный архив новой версии kot." };
        if (choose.ShowDialog(this) != DialogResult.OK) return;
        updateBusy = true; Snapshot();
        try { await InstallPackage(choose.FileName, ct: lifetime.Token); }
        finally { updateBusy = false; if (!exiting) Snapshot(); }
    }
    async Task InstallPackage(string path, string? advertisedVersion = null, CancellationToken ct = default)
    {
        if (new FileInfo(path).Length > RemoteUpdates.MaximumArchive) throw new UserError("Архив обновления слишком большой.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var manifest = await Task.Run(() => ReleasePackage.Validate(archive, new Version(ClientIdentity.Version)), ct);
        ct.ThrowIfCancellationRequested();
        if (advertisedVersion != null && manifest.Version != advertisedVersion) throw new UserError("Версия архива не совпадает с источником обновления.");
        string root = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        UpdateTransaction.SafePath(root);
        string work = Path.Combine(root, ".kot-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        bool launched = false;
        try
        {
            // Copy the exact already-validated bytes, rather than reopening a mutable source path.
            file.Position = 0; using (var output = new FileStream(Path.Combine(work, "package.zip"), FileMode.CreateNew, FileAccess.Write)) await file.CopyToAsync(output, ct);
            File.Copy(Environment.ProcessPath!, Path.Combine(work, "Updater.exe"));
            using var current = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(work, "request.json"), JsonSerializer.Serialize(new UpdateRequest(manifest.Version, current.Id, current.StartTime.ToUniversalTime().Ticks, reconnect.Desired, profile.Startup)));
            ct.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(Path.Combine(work, "Updater.exe")) { UseShellExecute = false, WorkingDirectory = work }; start.ArgumentList.Add("--apply-update");
            using var helper = Process.Start(start) ?? throw new UserError("Не удалось запустить установку обновления."); launched = true;
            AppLog.Write("update", "verified " + manifest.Version + "; stopping VPN and restarting"); await ExitApp();
        }
        finally { if (!launched) Directory.Delete(work, true); }
    }
}
internal sealed record UpdateRequest(string Version, int ParentPid, long ParentStarted, bool Resume, bool Startup);
internal static class UpdateInstaller
{
    public static string? Receipt;
    public static string MutexName => "Local\\KotVPN-Update-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
    public static void Ready() { if (Receipt != null) File.WriteAllText(Path.Combine(Receipt, "ready"), "ok"); }
    public static void Apply()
    {
        string work = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), root = Path.GetDirectoryName(work)!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(work), "^\\.kot-update-[a-f0-9]{32}$")) throw new UserError("Некорректная папка установщика.");
        var transaction = new UpdateTransaction(root, work);
        using var barrier = new Mutex(false, MutexName); bool owned = false, applied = false, parentClosed = false, committed = false;
        Process? next = null; UpdateRequest? request = null;
        try
        {
            try { owned = barrier.WaitOne(90000); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new UserError("Другая установка обновления ещё выполняется.");
            var requestPath = Path.Combine(work, "request.json");
            if (new FileInfo(requestPath).Length > 4096) throw new UserError("Некорректный запрос установки.");
            request = JsonSerializer.Deserialize<UpdateRequest>(File.ReadAllText(requestPath)) ?? throw new UserError("Пустой запрос установки.");
            try { using var parent = Process.GetProcessById(request.ParentPid); if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStarted || !parent.WaitForExit(60000)) throw new UserError("Предыдущая версия не закрылась."); } catch (ArgumentException) { }
            parentClosed = true;
            using (var zip = ZipFile.OpenRead(Path.Combine(work, "package.zip"))) transaction.Apply(zip, new Version(ClientIdentity.Version), request.Version);
            applied = true;
            var start = new ProcessStartInfo(Path.Combine(root, "Kot.exe")) { UseShellExecute = false, WorkingDirectory = root };
            start.ArgumentList.Add("--update-receipt"); start.ArgumentList.Add(work);
            if (request.Resume) start.ArgumentList.Add("--resume-connection");
            next = Process.Start(start) ?? throw new UserError("Не удалось запустить новую версию.");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(work, "ready")))
            {
                if (next.HasExited || deadline.Elapsed.TotalSeconds > 90) throw new UserError("Новая версия не запустила интерфейс. Выполняется откат.");
                Thread.Sleep(200);
            }
            transaction.Commit(); committed = true;
            try
            {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN", true);
            if (string.Equals(key?.GetValue("InstallLocation") as string, root, StringComparison.OrdinalIgnoreCase)) key?.SetValue("DisplayVersion", request.Version);
            } catch (Exception ex) { try { File.WriteAllText(Path.Combine(work, "registry-warning.txt"), Store.Friendly(ex)); } catch { } }
        }
        catch (Exception ex)
        {
            bool restored = parentClosed && !applied && (!File.Exists(Path.Combine(work, "journal.json")) || File.Exists(Path.Combine(work, "rolled-back")));
            string error = Store.Friendly(ex);
            if (applied && !committed)
            {
                try
                {
                    if (next != null && !next.HasExited) { next.Kill(); if (!next.WaitForExit(10000)) throw new IOException("Новая версия не закрылась для отката."); }
                    transaction.Rollback(); restored = true;
                }
                catch (Exception rollback) { error += "\nНе удалось завершить откат: " + Store.Friendly(rollback) + "\nРезервные файлы сохранены в " + work; }
            }
            try { File.WriteAllText(Path.Combine(work, "error.txt"), error); } catch { }
            MessageBox.Show("Обновление не установлено. " + error, "kot.", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (owned) { barrier.ReleaseMutex(); owned = false; }
            if (restored) { var start = new ProcessStartInfo(Path.Combine(root, "Kot.exe")) { UseShellExecute = false, WorkingDirectory = root }; if (request?.Resume == true) start.ArgumentList.Add("--resume-connection"); Process.Start(start)?.Dispose(); }
        }
        finally { next?.Dispose(); if (owned) barrier.ReleaseMutex(); }
    }
}
