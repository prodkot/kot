using Kot.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Kot.Windows;

// Manages owned packages and child jobs. Portable also bounds the lifetime of its WinDivert driver.
internal sealed class ManagedServices
{
    sealed class Entry(string id)
    {
        public string Id = id, Status = "Не установлен", Error = "", Phase = "";
        public ServiceInstallation? Installed;
        public ServiceRelease? Latest;
        public DateTimeOffset? Checked;
        public NativeCore? Process;
        public PortableDriver? Driver;
        public CancellationTokenSource? Attempt;
        public Task Work = Task.CompletedTask;
        public bool Busy;
        public double Progress;
        public DateTime? LogCleared;
        public readonly Queue<string> Log = new();
    }
    readonly Entry[] entries = [new("zapret"), new("telegram")];
    readonly Func<ServiceOptions> options;
    readonly Func<bool> networkBusy;
    readonly Action changed;
    readonly string folder;
    readonly CancellationToken lifetime;
    readonly JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    List<StrategyTestRun> history = [];
    StrategyTestRun? test;
    bool shuttingDown, polling;
    DateTimeOffset nextCheck = DateTimeOffset.UtcNow.AddSeconds(20);
    public bool Testing { get; private set; }
    public ManagedServices(Func<ServiceOptions> options, Func<bool> networkBusy, Action changed, CancellationToken lifetime, string? folder = null)
    {
        this.options = options; this.networkBusy = networkBusy; this.changed = changed; this.lifetime = lifetime;
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
        RegisterSecret();
    }
    Entry Get(string id) { ServicePackages.Repository(id); return entries.Single(e => e.Id == id); }
    string Root(string id) => Path.Combine(folder, id);
    string Manifest(string id) => Path.Combine(Root(id), "current.json");
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
    void RegisterSecret() { AppLog.Secret(options().Telegram.Secret); AppLog.Secret("dd" + options().Telegram.Secret); }
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
                running = e.Process != null && !e.Process.HasExited, e.Busy, e.Status, e.Error, e.Phase, e.Progress,
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
    string[] Strategies(Entry entry)
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(Exe(entry)))!;
        return Directory.GetFiles(root, "*.bat").Where(s => ZapretStrategy.ValidName(Path.GetFileName(s))).OrderBy(s => Path.GetFileName(s).Equals("general.bat", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(Path.GetFileName).Take(64).ToArray();
    }
    public void Begin(string id, string action, string[]? selected = null)
    {
        if (shuttingDown) throw new UserError("Приложение завершает работу.");
        var entry = Get(id); if (entry.Busy) throw new UserError("Дождитесь завершения операции или отмените её.");
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
    async Task Check(Entry entry, CancellationToken ct)
    {
        entry.Latest = await ServicePackages.Latest(entry.Id, ct); entry.Checked = DateTimeOffset.UtcNow;
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
            await ServicePackages.Download(release, download, new Progress<double>(p => { entry.Progress = p; }), ct);
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
    void PrepareLists(string root)
    {
        string lists = Path.Combine(root, "lists"); if (!Directory.Exists(lists)) throw new UserError("В пакете отсутствуют списки zapret.");
        var value = options().Zapret;
        Atomic(Path.Combine(lists, "list-general-user.txt"), Encoding.UTF8.GetBytes("# Managed by kot\n" + string.Join('\n', Configuration.Domains(value.Domains).DefaultIfEmpty("domain.example.abc")) + "\n"));
        Atomic(Path.Combine(lists, "list-exclude-user.txt"), Encoding.UTF8.GetBytes("# Managed by kot\n" + string.Join('\n', Configuration.Domains(value.ExcludedDomains).DefaultIfEmpty("domain.example.abc")) + "\n"));
        if (!File.Exists(Path.Combine(lists, "ipset-exclude-user.txt"))) Atomic(Path.Combine(lists, "ipset-exclude-user.txt"), Encoding.UTF8.GetBytes("# User exclusions\n203.0.113.0/24\n"));
    }
    async Task Start(Entry entry, string? strategy, CancellationToken ct)
    {
        if (entry.Process != null && !entry.Process.HasExited) return;
        if (entry.Process != null) { entry.Process.Dispose(); entry.Process = null; }
        ct.ThrowIfCancellationRequested(); string exe = Exe(entry); RegisterSecret(); options().Validate();
        if (entry.Id == "telegram")
        {
            if (await PortOpen(options().Telegram.Port, ct)) throw new UserError("Локальный порт Telegram занят. Измените порт или остановите другой прокси.");
            string data = Path.Combine(Path.GetDirectoryName(exe)!, "TgWsProxy_data"); Directory.CreateDirectory(data);
            Atomic(Path.Combine(data, "config.json"), options().Telegram.PortableConfig());
            File.WriteAllText(Path.Combine(data, ".first_run_done_mtproto"), "managed by kot"); File.WriteAllText(Path.Combine(data, ".ipv6_warned"), "see kot service help");
        }
        string[] arguments;
        if (entry.Id == "zapret")
        {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(exe))!; PrepareLists(root);
            string selected = strategy ?? options().Zapret.Strategy;
            string path = Strategies(entry).FirstOrDefault(s => Path.GetFileName(s) == selected) ?? throw new UserError("Стратегия не найдена в установленном пакете.");
            arguments = ZapretStrategy.Parse(File.ReadAllText(path), Path.GetDirectoryName(exe)!, Path.Combine(root, "lists"), options().Zapret.GameFilter);
        }
        else arguments = ["--portable"];
        entry.Status = "Запускается";
        try
        {
            if (entry.Id == "zapret" && AppRuntime.Portable) entry.Driver = await PortableDriver.Start(exe, ct);
            entry.Process = new NativeCore(exe, arguments, Path.GetDirectoryName(exe)!, "kot. " + entry.Id, line => AddLog(entry, line));
            if (entry.Id == "telegram")
            {
                bool ready = false;
                for (int i = 0; i < 80; i++) { ct.ThrowIfCancellationRequested(); if (entry.Process.HasExited) break; if (await PortOpen(options().Telegram.Port, ct)) { ready = true; break; } await Task.Delay(250, ct); }
                if (!ready || entry.Process.HasExited) throw new UserError("Telegram-прокси не открыл локальный порт. Проверьте журнал и наличие другой копии TgWsProxy.");
            }
            else { await Task.Delay(1200, ct); if (entry.Process.HasExited) throw new UserError("zapret завершился при запуске. Проверьте WinDivert, права и журнал."); }
            entry.Status = "Работает"; AddLog(entry, "Запущен" + (entry.Id == "zapret" ? ": " + (strategy ?? options().Zapret.Strategy) : " на 127.0.0.1:" + options().Telegram.Port));
        }
        catch { await Stop(entry); throw; }
    }
    internal static async Task<bool> PortOpen(int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(500);
        try { using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token); return true; }
        catch (SocketException) { return false; } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
    async Task Stop(Entry entry)
    {
        var process = entry.Process; entry.Process = null;
        try
        {
            if (process != null) await process.Stop();
        }
        finally
        {
            try { var driver = entry.Driver; entry.Driver = null; if (driver != null) await driver.Stop(); }
            finally
            {
                if (entry.Id == "telegram" && entry.Installed != null) TryDelete(Path.Combine(Path.GetDirectoryName(Exe(entry))!, "TgWsProxy_data", "config.json"));
                entry.Status = entry.Installed == null ? "Не установлен" : "Остановлен";
            }
        }
        if (process != null) AddLog(entry, "Остановлен вместе с дочерними процессами");
    }
    async Task Test(Entry entry, string[]? selected, CancellationToken ct)
    {
        var value = options().Zapret; value.Validate(); var targets = ServiceProbe.Targets(value.Targets);
        var available = Strategies(entry).Select(Path.GetFileName).ToArray();
        var strategies = selected == null || selected.Length == 0 ? available : selected.Distinct().ToArray();
        if (strategies.Length is < 1 or > 64 || strategies.Any(s => !available.Contains(s))) throw new UserError("Выберите стратегии из установленного пакета.");
        bool running = entry.Process != null && !entry.Process.HasExited; Testing = true;
        test = new StrategyTestRun { Version = entry.Installed!.Version }; changed();
        try
        {
            entry.Phase = "Проверка без zapret"; await Stop(entry); await Task.Delay(600, ct);
            test.Baseline = await ServiceProbe.Run(targets, value.TimeoutSeconds, ct); SaveHistory(test);
            for (int i = 0; i < strategies.Length; i++)
            {
                ct.ThrowIfCancellationRequested(); if (networkBusy()) throw new UserError("Подключение изменилось. Подбор остановлен, результаты сохранены.");
                string strategy = strategies[i]!; entry.Phase = $"{i + 1}/{strategies.Length}: {strategy}"; entry.Progress = i / (double)strategies.Length; changed();
                try
                {
                    await Start(entry, strategy, ct); await Task.Delay(800, ct);
                    var checks = await ServiceProbe.Run(targets, value.TimeoutSeconds, ct);
                    if (entry.Process == null || entry.Process.HasExited) test.Results.Add(new(strategy, checks, "Процесс завершился во время теста"));
                    else test.Results.Add(new(strategy, checks));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { test.Results.Add(new(strategy, [], Store.Friendly(ex))); }
                finally { await Stop(entry); }
                SaveHistory(test); changed();
            }
            entry.Progress = 1; AddLog(entry, test.Recommended == null ? "Подбор завершён: улучшение относительно прямого подключения не обнаружено." : "Рекомендована стратегия " + test.Recommended);
        }
        catch { test.Cancelled = true; throw; }
        finally
        {
            try { await Stop(entry); }
            finally
            {
                try { SaveHistory(test); }
                finally
                {
                    try { if (running && !shuttingDown && !lifetime.IsCancellationRequested) await Start(entry, null, CancellationToken.None); }
                    finally { Testing = false; }
                }
            }
        }
    }
    void SaveHistory(StrategyTestRun result)
    {
        if (!history.Contains(result)) history.Add(result); history = history.TakeLast(10).ToList();
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(history, json);
        while (data.Length > 1024 * 1024 && history.Count > 1) { history.RemoveAt(0); data = JsonSerializer.SerializeToUtf8Bytes(history, json); }
        Atomic(Path.Combine(folder, "tests.json"), data);
    }
    public Task Wait(string id) => Get(id).Work;
    public int? ProcessId(string id) => Get(id).Process?.Process.Id;
    public bool IsRunning(string id) => Get(id).Process != null && !Get(id).Process!.HasExited;
    public void Cancel(string id) => Get(id).Attempt?.Cancel();
    public void EnsureStrategy(string name)
    {
        var entry = Get("zapret");
        if (entry.Installed != null && !Strategies(entry).Any(s => Path.GetFileName(s) == name)) throw new UserError("Стратегия отсутствует в установленном пакете.");
    }
    public void EnsureEditable(string id) { if (Get(id).Busy || Testing) throw new UserError("Дождитесь завершения текущей операции."); }
    public void ClearLog(string id) { var e = Get(id); e.LogCleared = DateTime.Now; lock (e.Log) e.Log.Clear(); if (id == "telegram" && e.Installed != null && e.Process == null) { string path = Path.Combine(Path.GetDirectoryName(Exe(e))!, "TgWsProxy_data", "proxy.log"); TryDelete(path); TryDelete(path + ".1"); } }
    public void SelectTest(DateTimeOffset started)
    {
        if (Testing || Get("zapret").Busy) throw new UserError("Дождитесь окончания подбора.");
        test = history.FirstOrDefault(h => h.Started == started) ?? throw new UserError("Сохранённый прогон не найден.");
    }
    public string Export() => JsonSerializer.Serialize(test ?? history.LastOrDefault() ?? throw new UserError("Результатов теста пока нет."), json);
    public async Task AutoStart()
    {
        foreach (var entry in entries)
            if (entry.Installed != null && (entry.Id == "zapret" ? options().Zapret.AutoStart : options().Telegram.AutoStart)) { Begin(entry.Id, "start"); await entry.Work; }
    }
    public async Task Tick()
    {
        if (polling || shuttingDown) return; polling = true;
        try
        {
            foreach (var entry in entries)
                if (!entry.Busy && entry.Process != null && (entry.Process.HasExited || entry.Id == "telegram" && !await PortOpen(options().Telegram.Port, lifetime)))
                {
                    AddLog(entry, entry.Process.HasExited ? "Процесс завершился, код " + entry.Process.ExitCode : "Локальный порт прокси больше не доступен");
                    entry.Error = "Сервис завершился. Проверьте журнал и запустите его снова."; await Stop(entry); changed();
                }
            if (DateTimeOffset.UtcNow >= nextCheck && options().CheckUpdates && !Testing)
            {
                nextCheck = DateTimeOffset.UtcNow.AddHours(24);
                foreach (var entry in entries) if (!entry.Busy) Begin(entry.Id, "check");
            }
        }
        finally { polling = false; }
    }
    public async Task StopAll(bool exit = false)
    {
        if (exit) shuttingDown = true;
        foreach (var entry in entries) entry.Attempt?.Cancel();
        try { await Task.WhenAll(entries.Select(e => e.Work)); }
        finally { await Task.WhenAll(entries.Select(Stop)); }
    }
    static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
internal sealed record ServiceInstallation(string Version, string Executable, string Sha256);
