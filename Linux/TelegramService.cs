using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kot.Core;
namespace Kot.Linux;

public sealed class TelegramService
{
    readonly string folder = Paths.Private(Path.Combine(Paths.Data, "services/telegram"));
    readonly Func<TelegramProxyOptions> options;
    readonly Func<int> proxyPort;
    readonly Func<bool> downloadsAllowed;
    ServiceRelease? installed, latest;
    OwnedProcess? process;
    CancellationTokenSource? attempt;
    CancellationTokenSource? logAttempt;
    Task logWork = Task.CompletedTask;
    int logFd = -1;
    string? logPipe;
    readonly Queue<string> logs = new();
    public string Error { get; private set; } = "";
    public string Status { get; private set; } = "Не установлен";
    public bool Busy => attempt != null;
    public bool Running => process != null && !process.Exited;
    public Task Work { get; private set; } = Task.CompletedTask;
    string Manifest => Path.Combine(folder, "current.json");
    string Executable(ServiceRelease release) => Path.Combine(folder, release.Version, "TgWsProxy_linux_amd64");
    string Config(ServiceRelease release) => Path.Combine(folder, release.Version, "TgWsProxy_data/config.json");
    public TelegramService(Func<TelegramProxyOptions> options, Func<int> proxyPort, Func<bool> downloadsAllowed)
    {
        this.options = options; this.proxyPort = proxyPort; this.downloadsAllowed = downloadsAllowed;
        if (File.Exists(Manifest))
        {
            if (new FileInfo(Manifest).Length > 8192) throw new UserError("Повреждён манифест Telegram.");
            installed = JsonSerializer.Deserialize<ServiceRelease>(File.ReadAllBytes(Manifest));
            if (installed == null || installed.Id != "telegram" || installed.Platform != ServicePlatform.Linux || !ServicePackages.ValidVersion(installed.Version)) throw new UserError("Некорректный Linux-пакет Telegram.");
            if (!Verify(installed)) throw new UserError("SHA-256 Linux Telegram не совпал. Пакет не запущен.");
            if (File.Exists(Config(installed))) File.Delete(Config(installed)); Status = "Остановлен";
        }
    }
    bool Verify(ServiceRelease release)
    {
        string path = Executable(release);
        if (!File.Exists(path) || new FileInfo(path).LinkTarget != null || new FileInfo(path).Length != release.Size) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream)) == release.Sha256;
    }
    public object Model() => new { id = "telegram", installedVersion = installed?.Version, availableVersion = latest?.Version, updateAvailable = latest != null && ServicePackages.Newer(latest.Version, installed?.Version ?? ""), busy = Busy, running = Running, status = Status, error = Error, logs = Logs(), source = ServicePackages.Page("telegram") };
    string[] Logs()
    {
        string[] saved; lock (logs) saved = logs.ToArray();
        string text = string.Join('\n', saved) + "\n" + (process?.Log.Snapshot() ?? "");
        return CoreDiagnostics.CreateRedactor([], options().Secret)(text).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(120).Select(line => line.Length > 1500 ? line[..1500] : line).ToArray();
    }
    void StartLogPipe(string data)
    {
        // Upstream logs its MTProto link at startup. A FIFO, not a regular log
        // file, ensures those bytes are redacted BEFORE entering any journal.
        logPipe = Path.Combine(data, "proxy.log");
        File.Delete(logPipe); File.Delete(logPipe + ".1");
        if (mkfifo(logPipe, 0x180) != 0) throw new UserError("Не удалось создать защищённый канал журнала Telegram.");
        logFd = open(logPipe, 2 | 0x800 | 0x80000); // RDWR, NONBLOCK, CLOEXEC
        if (logFd < 0) { File.Delete(logPipe); throw new UserError("Канал журнала Telegram недоступен."); }
        logAttempt = new CancellationTokenSource(); logWork = ReadLog(logAttempt.Token);
    }
    async Task ReadLog(CancellationToken ct)
    {
        byte[] buffer = new byte[4096]; char[] chars = new char[4096]; var decoder = Encoding.UTF8.GetDecoder(); var line = new StringBuilder(); bool oversized = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int count = (int)read(logFd, buffer, (nuint)buffer.Length);
                if (count <= 0) { await Task.Delay(50, ct); continue; }
                int length = decoder.GetChars(buffer, 0, count, chars, 0);
                for (int i = 0; i < length; i++)
                {
                    if (chars[i] == '\n')
                    {
                        string clean = oversized ? "[Слишком длинная строка upstream скрыта]" : CoreDiagnostics.CreateRedactor([], options().Secret)(line.ToString());
                        lock (logs) { logs.Enqueue(clean.Length > 1500 ? clean[..1500] : clean); while (logs.Count > 120) logs.Dequeue(); }
                        line.Clear(); oversized = false;
                    }
                    else if (line.Length < 8000) line.Append(chars[i]); else oversized = true;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async Task Tick()
    {
        if (!Busy && process?.Exited == true) { await Stop(); Error = "Telegram завершился. Просмотрите журнал сервиса."; }
    }
    public void Begin(string action)
    {
        if (Busy) throw new UserError("Дождитесь операции или отмените её.");
        attempt = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Work = Perform(action, attempt);
    }
    public void Cancel() => attempt?.Cancel();
    async Task Perform(string action, CancellationTokenSource owner)
    {
        Error = "";
        try
        {
            switch (action)
            {
                case "check": latest = await Latest(owner.Token); Status = installed == null ? "Не установлен" : Running ? "Работает" : "Остановлен"; break;
                case "install": await Install(owner.Token); break;
                case "start": await Start(owner.Token); break;
                case "stop": await Stop(); break;
                case "restart": await Stop(); await Start(owner.Token); break;
                case "remove": await Stop(); if (installed != null) { File.Delete(Executable(installed)); File.Delete(Manifest); installed = null; } Status = "Не установлен"; break;
                default: throw new UserError("Неизвестная операция Telegram.");
            }
        }
        catch (Exception e) { Error = e is UserError ? e.Message : e is OperationCanceledException ? "Операция отменена." : "Ошибка Telegram: " + e.GetType().Name; }
        finally { attempt = null; owner.Dispose(); }
    }
    async Task<ServiceRelease> Latest(CancellationToken ct)
    {
        if (!downloadsAllowed()) throw new UserError("Загрузка заблокирована kill switch. Подключите VPN.");
        return await ServicePackages.Latest("telegram", ct, proxyPort: proxyPort(), platform: ServicePlatform.Linux);
    }
    async Task Install(CancellationToken ct)
    {
        Status = "Загрузка официального Linux-пакета";
        latest = await Latest(ct);
        if (installed != null && installed.Version == latest.Version && Verify(installed)) { Status = Running ? "Работает" : "Остановлен"; return; }
        string directory = Paths.Private(Path.Combine(folder, latest.Version)), destination = Executable(latest), temporary = destination + ".download-" + Guid.NewGuid().ToString("N");
        bool wasRunning = Running; var old = installed;
        try
        {
            await ServicePackages.Download(latest, temporary, null, ct, proxyPort: proxyPort());
            using (var stream = File.OpenRead(temporary)) { byte[] elf = new byte[20]; stream.ReadExactly(elf); if (!elf.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 69, 76, 70 }) || elf[4] != 2 || elf[5] != 1 || elf[18] != 62) throw new UserError("Пакет не является Linux x86_64 ELF."); }
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await Stop(); ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, true); installed = latest;
            File.WriteAllText(Path.Combine(directory, "LICENSE.txt"), ServicePackages.TelegramLicense);
            if (wasRunning) await Start(ct);
            Paths.Atomic(Manifest, JsonSerializer.SerializeToUtf8Bytes(installed)); Status = Running ? "Работает" : "Остановлен";
        }
        catch
        {
            await Stop(); installed = old;
            if (wasRunning && old != null) { try { await Start(CancellationToken.None); } catch { Status = "Предыдущая версия не запущена"; } }
            throw;
        }
        finally { File.Delete(temporary); }
    }
    public async Task Start(CancellationToken ct)
    {
        if (Running) return;
        if (installed == null || !Verify(installed)) throw new UserError("Установите проверенный Linux-пакет Telegram.");
        var value = options(); value.Validate();
        if (await PortOpen(value.Port, ct)) throw new UserError("Порт Telegram занят.");
        string data = Paths.Private(Path.GetDirectoryName(Config(installed))!);
        Paths.Atomic(Config(installed), value.ServiceConfig());
        File.WriteAllText(Path.Combine(data, ".first_run_done_mtproto"), "managed by kot");
        File.WriteAllText(Path.Combine(data, ".ipv6_warned"), "see kot documentation");
        try
        {
            StartLogPipe(data);
            process = new OwnedProcess(Executable(installed), ["--portable"]);
            for (int i = 0; i < 80; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (process.Exited) throw new UserError("Linux Telegram завершился. Проверьте доступность GUI/библиотек upstream.");
                if (await PortOpen(value.Port, ct)) { Status = "Работает"; return; }
                await Task.Delay(250, ct);
            }
            throw new UserError("Telegram не открыл локальный порт за 20 секунд.");
        }
        catch { await Stop(); throw; }
    }
    static async Task<bool> PortOpen(int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(200);
        try { using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token); return true; }
        catch (Exception e) when (e is SocketException || e is OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }
    public async Task Stop()
    {
        var old = process; process = null;
        try { if (old != null) await old.DisposeAsync(); }
        finally
        {
            logAttempt?.Cancel(); await logWork;
            if (logFd >= 0) { close(logFd); logFd = -1; }
            if (logPipe != null) { File.Delete(logPipe); logPipe = null; }
            logAttempt?.Dispose(); logAttempt = null;
            if (installed != null && File.Exists(Config(installed))) File.Delete(Config(installed)); Status = installed == null ? "Не установлен" : "Остановлен";
        }
    }
    public async Task Shutdown() { Cancel(); await Work; await Stop(); }
    [DllImport("libc", SetLastError = true)] static extern int mkfifo(string path, uint mode);
    [DllImport("libc", SetLastError = true)] static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] static extern nint read(int fd, byte[] buffer, nuint count);
    [DllImport("libc")] static extern int close(int fd);
}
