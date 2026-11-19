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
    string[] Strategies(Entry entry)
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(Exe(entry)))!;
        return Directory.GetFiles(root, "*.bat").Where(s => ZapretStrategy.ValidName(Path.GetFileName(s))).OrderBy(s => Path.GetFileName(s).Equals("general.bat", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(Path.GetFileName).Take(64).ToArray();
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
}
