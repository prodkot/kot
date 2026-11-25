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
        ct.ThrowIfCancellationRequested(); string exe = Exe(entry); options().Validate();
        PrepareIntegrity(entry);
        if (entry.Id == "telegram")
        {
            if (await PortOpen(options().Telegram.Port, ct)) throw new UserError("Локальный порт Telegram занят. Измените порт или остановите другой прокси.");
            string data = Path.Combine(Path.GetDirectoryName(exe)!, "TgWsProxy_data"); Directory.CreateDirectory(data);
            // The official application detects this pre-populated directory beside its exe.
            // Keep its settings separate from independently installed TgWsProxy copies.
            Atomic(Path.Combine(data, "config.json"), options().Telegram.ServiceConfig());
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
        else arguments = [];
        entry.Status = "Запускается";
        try
        {
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
            if (entry.Id == "telegram" && entry.Installed != null) TryDelete(Path.Combine(Path.GetDirectoryName(Exe(entry))!, "TgWsProxy_data", "config.json"));
            entry.Status = entry.Installed == null ? "Не установлен" : "Остановлен";
        }
        if (process != null) AddLog(entry, "Остановлен вместе с дочерними процессами");
    }
}
