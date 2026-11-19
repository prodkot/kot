using Kot.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Kot.Windows;

// Manages owned packages and child jobs without stopping shared Windows drivers.
internal sealed partial class ManagedServices
{
    sealed class Entry(string id)
    {
        public string Id = id, Status = "Не установлен", Error = "", Phase = "";
        public ServiceInstallation? Installed;
        public ServiceRelease? Latest;
        public DateTimeOffset? Checked;
        public NativeCore? Process;
        public CancellationTokenSource? Attempt;
        public Task Work = Task.CompletedTask;
        public bool Busy;
        public double Progress;
        public DateTime? LogCleared;
        public readonly Queue<string> Log = new();
    }
    readonly Entry[] entries = [new("zapret"), new("telegram")];
    readonly Func<ServiceOptions> options;
    readonly Func<bool> networkBusy, downloadsAvailable;
    readonly Func<int> proxyPort;
    readonly Func<HttpMessageHandler>? metadataTransport;
    readonly Action changed;
    readonly string folder;
    readonly CancellationToken lifetime;
    readonly JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    List<StrategyTestRun> history = [];
    StrategyTestRun? test;
    bool shuttingDown, polling, preparingVpn;
    Task vpnPreparation = Task.CompletedTask;
    DateTimeOffset nextCheck = DateTimeOffset.UtcNow.AddSeconds(20);
    public bool Testing { get; private set; }
    public ManagedServices(Func<ServiceOptions> options, Func<bool> networkBusy, Action changed, CancellationToken lifetime, string? folder = null, Func<int>? proxyPort = null, Func<bool>? downloadsAvailable = null, Func<HttpMessageHandler>? metadataTransport = null)
    {
        this.options = options; this.networkBusy = networkBusy; this.changed = changed; this.lifetime = lifetime;
        this.proxyPort = proxyPort ?? (() => 0); this.downloadsAvailable = downloadsAvailable ?? (() => true);
        this.metadataTransport = metadataTransport;
        this.folder = folder ?? Path.Combine(Store.Folder, "services"); Directory.CreateDirectory(this.folder);
        foreach (var entry in entries)
        {
            try { entry.Installed = ReadInstallation(entry.Id); if (entry.Installed != null) entry.Status = "Остановлен"; }
            catch (Exception ex) { entry.Error = Store.Friendly(ex); entry.Status = "Нужна переустановка"; }
        }
        try
        {
            string path = Path.Combine(this.folder, "tests.json");
            if (File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024) history = JsonSerializer.Deserialize<List<StrategyTestRun>>(File.ReadAllText(path), json)?.TakeLast(10).Where(r => r != null && r.Results.Count <= 64 && r.Baseline.Count <= 12 && r.Results.All(v => v.Checks.Count <= 12)).ToList() ?? [];
        }
        catch (Exception ex) { AddLog(Get("zapret"), "История тестов не прочитана: " + Store.Friendly(ex)); }
    }
    Entry Get(string id) { ServicePackages.Repository(id); return entries.Single(e => e.Id == id); }
    bool IsBusy(Entry entry) => entry.Busy || entry.Id == "zapret" && preparingVpn;
    void AddLog(Entry entry, string message)
    {
        string clean = AppLog.Clean(message); if (clean.Length > 1500) clean = clean[..1500];
        lock (entry.Log) { entry.Log.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + clean); while (entry.Log.Count > 120) entry.Log.Dequeue(); }
    }
    public object Model(bool full)
    {
        var value = options();
        return new
        {
            value.CheckUpdates, testing = Testing, networkBusy = networkBusy(),
            items = entries.Select(e => new
            {
                e.Id, installedVersion = e.Installed?.Version, availableVersion = e.Latest?.Version,
                updateAvailable = e.Latest != null && ServicePackages.Newer(e.Latest.Version, e.Installed?.Version ?? ""),
                running = e.Process != null && !e.Process.HasExited, busy = IsBusy(e), e.Status, e.Error, e.Phase, e.Progress,
                checkedAt = e.Checked, source = ServicePackages.Page(e.Id),
                logs = full ? LogTail(e) : null,
                strategies = e.Id == "zapret" && full && e.Installed != null ? Strategies(e).Select(s => Path.GetFileName(s)).ToArray() : null
            }).ToArray(),
            zapret = value.Zapret, telegram = new { value.Telegram.AutoStart, value.Telegram.Port, value.Telegram.Cloudflare, value.Telegram.Http2, value.Telegram.PoolSize, value.Telegram.DcIp, value.Telegram.Domains, value.Telegram.WorkerDomains },
            test = full ? test ?? history.LastOrDefault() : null, history = full ? history.TakeLast(10).Select(h => new { h.Started, h.Version, h.Cancelled, h.Recommended, count = h.Results.Count }).ToArray() : null
        };
    }
    string[] LogTail(Entry entry)
    {
        var logs = new List<string>(); lock (entry.Log) logs.AddRange(entry.Log);
        if (entry.Id == "telegram" && entry.Installed != null)
        {
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Exe(entry))!, "TgWsProxy_data", "proxy.log");
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    bool skip = stream.Length > 24000; if (skip) stream.Seek(-24000, SeekOrigin.End);
                    using var reader = new StreamReader(stream, Encoding.UTF8); if (skip) reader.ReadLine();
                    logs.AddRange(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(70).Where(line => entry.LogCleared == null || line.Length >= 19 && DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var stamp) && stamp > entry.LogCleared).Select(AppLog.Clean));
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return logs.TakeLast(120).ToArray();
    }
    public void Begin(string id, string action, string[]? selected = null)
    {
        if (shuttingDown) throw new UserError("Приложение завершает работу.");
        var entry = Get(id); if (IsBusy(entry)) throw new UserError("Дождитесь завершения операции или отмените её.");
        if (action == "test" && (id != "zapret" || networkBusy())) throw new UserError("Отключите VPN и kill switch перед подбором, чтобы проверить zapret на прямом соединении.");
        if (Testing && action != "check") throw new UserError("Сначала завершите подбор стратегии.");
        entry.Busy = true; entry.Error = ""; entry.Progress = 0;
        entry.Attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime); entry.Attempt.CancelAfter(TimeSpan.FromMinutes(action == "test" ? 25 : 5));
        entry.Work = Perform(entry, action, selected, entry.Attempt);
    }
    async Task Perform(Entry entry, string action, string[]? selected, CancellationTokenSource attempt)
    {
        try
        {
            switch (action)
            {
                case "check": entry.Phase = "Проверка обновлений"; await Check(entry, attempt.Token); break;
                case "install": entry.Phase = "Загрузка пакета"; await Install(entry, attempt.Token); break;
                case "start": entry.Phase = "Запуск"; await Start(entry, null, attempt.Token); break;
                case "restart": entry.Phase = "Перезапуск"; await Stop(entry); await Start(entry, null, attempt.Token); break;
                case "stop": entry.Phase = "Остановка"; await Stop(entry); break;
                case "remove":
                    entry.Phase = "Удаление пакета"; await Stop(entry);
                    bool deferred = ManagedPackageFiles.Remove(folder, Root(entry.Id), Manifest(entry.Id));
                    entry.Installed = null; entry.Status = deferred ? "Очистка после перезагрузки Windows" : "Не установлен";
                    AddLog(entry, deferred ? "Пакет удалён. Windows завершит удаление занятых файлов при перезагрузке." : "Пакет удалён; настройки и история сохранены."); break;
                case "test": await Test(entry, selected, attempt.Token); break;
                default: throw new UserError("Неизвестная операция сервиса.");
            }
        }
        catch (OperationCanceledException) { entry.Error = "Операция отменена или превышено время ожидания."; AddLog(entry, entry.Error); }
        catch (Exception ex) { entry.Error = Store.Friendly(ex); AddLog(entry, entry.Error); AppLog.Error("service " + entry.Id, ex); }
        finally { entry.Busy = false; entry.Phase = ""; if (entry.Attempt == attempt) entry.Attempt = null; attempt.Dispose(); if (!shuttingDown) changed(); }
    }
    public Task Wait(string id) => id == "zapret" && preparingVpn ? vpnPreparation : Get(id).Work;
    public int? ProcessId(string id) => Get(id).Process?.Process.Id;
    public bool IsRunning(string id) => Get(id).Process != null && !Get(id).Process!.HasExited;
    public void Cancel(string id) => Get(id).Attempt?.Cancel();
    public void EnsureStrategy(string name)
    {
        var entry = Get("zapret");
        if (entry.Installed != null && !Strategies(entry).Any(s => Path.GetFileName(s) == name)) throw new UserError("Стратегия отсутствует в установленном пакете.");
    }
    public void EnsureEditable(string id) { if (IsBusy(Get(id)) || Testing) throw new UserError("Дождитесь завершения текущей операции."); }
    public void ClearLog(string id) { var e = Get(id); e.LogCleared = DateTime.Now; lock (e.Log) e.Log.Clear(); if (id == "telegram" && e.Installed != null && e.Process == null) { string path = Path.Combine(Path.GetDirectoryName(Exe(e))!, "TgWsProxy_data", "proxy.log"); TryDelete(path); TryDelete(path + ".1"); } }
    public void SelectTest(DateTimeOffset started)
    {
        if (Testing || IsBusy(Get("zapret"))) throw new UserError("Дождитесь окончания подбора.");
        test = history.FirstOrDefault(h => h.Started == started) ?? throw new UserError("Сохранённый прогон не найден.");
    }
    public string Export() => JsonSerializer.Serialize(test ?? history.LastOrDefault() ?? throw new UserError("Результатов теста пока нет."), json);
    public Task StopZapretForVpn()
    {
        if (shuttingDown) throw new UserError("Приложение завершает работу.");
        if (Testing) throw new UserError("Завершите подбор zapret перед подключением VPN.");
        if (preparingVpn) return vpnPreparation;
        preparingVpn = true;
        vpnPreparation = StopZapretBeforeVpn(Get("zapret"));
        return vpnPreparation;
    }
    async Task StopZapretBeforeVpn(Entry entry)
    {
        bool active = entry.Process != null || entry.Busy;
        entry.Attempt?.Cancel();
        try
        {
            // A pending start or installation rollback can recreate the process.
            // Wait for that work before stopping the entire owned job.
            await entry.Work;
            entry.Busy = true; entry.Error = ""; entry.Phase = "Остановка перед VPN";
            if (!shuttingDown) changed();
            await Stop(entry);
            if (active) AddLog(entry, "Автоматически остановлен перед подключением VPN");
        }
        catch (Exception ex) { entry.Error = Store.Friendly(ex); throw; }
        finally { entry.Busy = false; entry.Phase = ""; preparingVpn = false; if (!shuttingDown) changed(); }
    }
    public async Task AutoStart(bool startZapret = true)
    {
        foreach (var entry in entries)
            if (!IsBusy(entry) && entry.Installed != null && (entry.Id == "zapret" ? startZapret && options().Zapret.AutoStart : options().Telegram.AutoStart)) { Begin(entry.Id, "start"); await entry.Work; }
    }
    public async Task Tick()
    {
        if (polling || shuttingDown) return; polling = true;
        try
        {
            foreach (var entry in entries)
            {
                if (IsBusy(entry) || entry.Process is not { } process) continue;
                if (!process.HasExited && (entry.Id != "telegram" || await PortOpen(options().Telegram.Port, lifetime))) continue;
                // A stop/restart can replace or dispose the process while the port check awaits.
                if (shuttingDown || lifetime.IsCancellationRequested || IsBusy(entry) || entry.Process != process) continue;
                AddLog(entry, process.HasExited ? "Процесс завершился, код " + process.ExitCode : "Локальный порт прокси больше не доступен");
                entry.Error = "Сервис завершился. Проверьте журнал и запустите его снова."; await Stop(entry); changed();
            }
            if (shuttingDown || lifetime.IsCancellationRequested) return;
            if (downloadsAvailable() && DateTimeOffset.UtcNow >= nextCheck && options().CheckUpdates && !Testing)
            {
                nextCheck = DateTimeOffset.UtcNow.AddHours(24);
                foreach (var entry in entries) if (!IsBusy(entry)) Begin(entry.Id, "check");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { polling = false; }
    }
    public async Task StopAll(bool exit = false)
    {
        if (exit) shuttingDown = true;
        foreach (var entry in entries) entry.Attempt?.Cancel();
        try { await Task.WhenAll(entries.Select(e => e.Work).Append(vpnPreparation)); }
        finally { await Task.WhenAll(entries.Select(Stop)); }
    }
    static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
internal sealed record ServiceInstallation(string Version, string Executable, string Sha256);
