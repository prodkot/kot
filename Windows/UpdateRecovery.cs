using Kot.Core;
using System.Diagnostics;
using System.Text.Json;
namespace Kot.Windows;

internal static partial class UpdateInstaller
{
    sealed record RecoveryRequest(int ParentPid, long ParentStarted, bool Startup, bool Resume);
    public static bool StartRecovery(string[] args)
    {
        string root = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string? work = UpdateTransaction.PendingRecovery(root);
        if (work == null) return false;
        bool resume = args.Contains("--resume-connection");
        string request = Path.Combine(work, "request.json");
        if (File.Exists(request) && new FileInfo(request).Length <= 4096)
        {
            try { resume |= JsonSerializer.Deserialize<UpdateRequest>(File.ReadAllText(request))?.Resume == true; }
            catch (JsonException) { }
        }
        string helper = Path.Combine(work, "Recovery.exe"); UpdateTransaction.SafePath(helper);
        File.Copy(Environment.ProcessPath!, helper, true);
        using var parent = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(work, "recovery.json"), JsonSerializer.Serialize(new RecoveryRequest(parent.Id, parent.StartTime.ToUniversalTime().Ticks, args.Contains("--startup"), resume)));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = work };
        start.ArgumentList.Add("--recover-update");
        using var process = Process.Start(start) ?? throw new UserError("Не удалось запустить восстановление обновления.");
        return true;
    }
    public static void Recover()
    {
        string work = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(work), "^\\.kot-update-[a-f0-9]{32}$")) throw new UserError("Некорректная папка восстановления.");
        string root = Path.GetDirectoryName(work)!; UpdateTransaction.SafePath(work);
        string path = Path.Combine(work, "recovery.json"); UpdateTransaction.SafePath(path);
        if (new FileInfo(path).Length > 4096) throw new UserError("Некорректный запрос восстановления.");
        var request = JsonSerializer.Deserialize<RecoveryRequest>(File.ReadAllText(path)) ?? throw new UserError("Пустой запрос восстановления.");
        using var barrier = new Mutex(false, MutexName);
        bool owned; try { owned = barrier.WaitOne(90000); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new UserError("Другая установка ещё выполняется.");
        try
        {
            try
            {
                using var parent = Process.GetProcessById(request.ParentPid);
                if (parent.StartTime.ToUniversalTime().Ticks == request.ParentStarted && !parent.WaitForExit(60000)) throw new UserError("Закройте kot. для восстановления обновления.");
            }
            catch (ArgumentException) { }
            UpdateTransaction.RecoverPending(root);
        }
        finally { barrier.ReleaseMutex(); }
        var start = new ProcessStartInfo(Path.Combine(root, "Kot.exe")) { UseShellExecute = false, WorkingDirectory = root };
        if (request.Startup) start.ArgumentList.Add("--startup");
        if (request.Resume) start.ArgumentList.Add("--resume-connection");
        using var next = Process.Start(start) ?? throw new UserError("Установка восстановлена. Запустите kot. ещё раз.");
    }
}
