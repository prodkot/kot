using Kot.Core;
using System.Diagnostics;
namespace Kot.Windows;
public sealed partial class MainWindow
{
    const string AppVersion = ClientIdentity.Version;
    DateTimeOffset lastUpdateCheck = DateTimeOffset.MinValue, nextUpdateCheck = DateTimeOffset.MinValue;
    UpdateRelease? remoteRelease;
    CancellationTokenSource? updateAttempt;
    bool updateBusy;
    long updateBytes;
    string updateStatus = "", updateError = "";
    object UpdateModel() => new { busy = updateBusy, bytes = updateBytes, status = updateStatus, error = updateError, availableVersion = remoteRelease?.Version, size = remoteRelease?.Size ?? 0, checkedAt = lastUpdateCheck == DateTimeOffset.MinValue ? null : lastUpdateCheck.ToLocalTime().ToString("dd.MM HH:mm") };
    void ScheduleUpdateCheck()
    {
        if (!updateBusy && DateTimeOffset.UtcNow >= nextUpdateCheck && operations.CurrentCount > 0 && !connecting) _ = CheckUpdates(automatic: true);
    }
    async Task CheckUpdates(bool automatic = false)
    {
        if (updateBusy) throw new UserError("Проверка или загрузка обновления уже идёт.");
        remoteRelease = null; updateBusy = true; updateStatus = "Проверка обновлений"; updateError = ""; updateBytes = 0; lastUpdateCheck = DateTimeOffset.UtcNow; nextUpdateCheck = lastUpdateCheck.AddHours(24); Snapshot();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); updateAttempt = attempt;
        try
        {
            remoteRelease = await RemoteUpdates.Check(new Version(AppVersion), attempt.Token);
            updateStatus = remoteRelease == null ? "Установлена последняя версия" : "Доступна версия " + remoteRelease.Version;
            AppLog.Write("update", remoteRelease == null ? "no newer release" : "release available: " + remoteRelease.Version);
        }
        catch (OperationCanceledException) { if (!attempt.IsCancellationRequested) nextUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(15); updateStatus = "Проверка отменена или превышено время ожидания"; }
        catch (Exception ex) { nextUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(15); AppLog.Error("update check", ex); updateError = Store.Friendly(ex); updateStatus = "Не удалось проверить обновления"; remoteRelease = null; }
        finally { if (updateAttempt == attempt) updateAttempt = null; updateBusy = false; if (!exiting) Snapshot(); }
        if (automatic && remoteRelease != null && !exiting) await DownloadUpdate();
    }
    async Task DownloadUpdate()
    {
        var release = remoteRelease ?? throw new UserError("Сначала проверьте обновления.");
        if (updateBusy) throw new UserError("Обновление уже загружается.");
        string temporary = Path.Combine(Store.Folder, "update-" + Guid.NewGuid().ToString("N") + ".exe");
        updateBusy = true; updateBytes = 0; updateError = ""; updateStatus = "Загрузка " + release.Version; Snapshot();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); updateAttempt = attempt;
        try
        {
            Directory.CreateDirectory(Store.Folder); long lastProgress = Stopwatch.GetTimestamp();
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await RemoteUpdates.Download(release, file, attempt.Token, bytes =>
                {
                    updateBytes = bytes; long now = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(lastProgress, now).TotalSeconds < .5) return; lastProgress = now;
                    if (!exiting && !IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!exiting) Snapshot(); });
                });
            attempt.Token.ThrowIfCancellationRequested(); updateStatus = "Проверка подписи"; Snapshot();
            await InstallPackage(temporary, release.Version, attempt.Token);
            if (!exiting) updateStatus = "Установка отменена";
        }
        catch (OperationCanceledException) { updateStatus = "Загрузка отменена или превышено время ожидания"; }
        catch (Exception ex) { AppLog.Error("update download", ex); updateError = Store.Friendly(ex); updateStatus = "Обновление не установлено"; }
        finally
        {
            try { File.Delete(temporary); } catch (Exception ex) { AppLog.Error("update cleanup", ex); }
            if (updateAttempt == attempt) updateAttempt = null; updateBusy = false; if (!exiting) Snapshot();
        }
    }
}
