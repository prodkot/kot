using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Kot.Core;
namespace Kot.Linux;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux() || geteuid() == 0) { Console.Error.WriteLine("kot. GUI/backend должен работать без root."); return 1; }
        umask(0x3f); // 0077, including portable Core persistence writers
        try
        {
            if (args.SequenceEqual(new[] { "--restore-proxy" })) { await new GnomeProxy().Restore(); return 0; }
            if (!args.SequenceEqual(new[] { "--backend" })) { Console.Error.WriteLine("Запуск: /usr/bin/kot (GTK shell)."); return 2; }
            using var store = await Store.Open();
            await new Backend(store).Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex is UserError ? ex.Message : "Ошибка kot.: " + ex.GetType().Name + ". Существующие данные не удалены.");
            return 1;
        }
    }
    [DllImport("libc")] static extern uint geteuid();
    [DllImport("libc")] static extern uint umask(uint mask);
}
