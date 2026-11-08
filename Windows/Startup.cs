using System.Runtime.InteropServices;
using System.Security.Principal;
using Kot.Core;
using Microsoft.Win32;
using System.Xml.Linq;

namespace Kot.Windows;

public static class Startup
{
    const int CreateOrUpdate = 6, InteractiveToken = 3, HighestAvailable = 1;
    const int LogonTrigger = 9, ExecAction = 0, IgnoreNew = 2;
    static string Sid
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; }
    }
    static bool Missing(Exception ex) => ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003);

    public static Task Set(bool enabled)
    {
        RequireInstalledOwner();
        string sid = Sid;
        return Set(enabled, "KotVPN-" + sid, sid, Environment.ProcessPath!, AppContext.BaseDirectory);
    }

    public static async Task<bool> IsEnabled() => (await Inspect("KotVPN-" + Sid, Sid, Environment.ProcessPath!, AppContext.BaseDirectory)).Healthy;

    internal static string? InstalledExecutable()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN");
        return key?.GetValue("InstallLocation") is string path && !string.IsNullOrWhiteSpace(path) ? Path.Combine(path, "Kot.exe") : null;
    }
    internal static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }
    static void RequireInstalledOwner()
    {
        if (!SamePath(InstalledExecutable(), Environment.ProcessPath))
            throw new UserError("Автозапуск настраивается из установленной копии kot. Запустите её из меню «Пуск» или повторите установку.");
    }
    internal static string? ResolveSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return new SecurityIdentifier(value).Value; } catch (ArgumentException) { }
        try { return ((SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier))).Value; }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException) { return null; }
    }
    internal record State(bool Exists, bool Enabled, bool Healthy);
    internal static bool ValidDefinition(string xml, string sid, string executable, string directory)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root = XDocument.Parse(xml).Root!;
        var actions = root.Element(ns + "Actions")?.Elements().ToArray() ?? [];
        var triggers = root.Element(ns + "Triggers")?.Elements().ToArray() ?? [];
        var principals = root.Element(ns + "Principals")?.Elements().ToArray() ?? [];
        if (actions.Length != 1 || actions[0].Name != ns + "Exec" || triggers.Length != 1 || triggers[0].Name != ns + "LogonTrigger" || principals.Length != 1) return false;
        string? Text(XElement? e, string name) => (string?)e?.Element(ns + name);
        bool False(XElement? e, string name) => Text(e, name) is null or "false";
        var a = actions[0]; var t = triggers[0]; var p = principals[0]; var s = root.Element(ns + "Settings");
        return File.Exists(executable) && SamePath(Text(a, "Command"), executable) && SamePath(Text(a, "WorkingDirectory"), directory)
            && Text(a, "Arguments") == "--startup" && ResolveSid(Text(t, "UserId")) == sid && Text(t, "Enabled") != "false"
            && string.IsNullOrEmpty(Text(t, "EndBoundary")) && string.IsNullOrEmpty(Text(t, "StartBoundary"))
            && Text(t, "Delay") is null or "PT0S"
            && ResolveSid(Text(p, "UserId")) == sid && Text(p, "LogonType") == "InteractiveToken" && Text(p, "RunLevel") == "HighestAvailable"
            && s != null && Text(s, "Enabled") != "false" && False(s, "RunOnlyIfIdle") && False(s, "RunOnlyIfNetworkAvailable")
            && False(s, "DisallowStartIfOnBatteries") && False(s, "StopIfGoingOnBatteries")
            && Text(s, "ExecutionTimeLimit") == "PT0S" && Text(s, "MultipleInstancesPolicy") == "IgnoreNew"
            && Text(s, "StartWhenAvailable") == "true" && Text(s, "AllowStartOnDemand") != "false";
    }
    internal static Task<State> Inspect(string name, string sid, string executable, string directory) => Task.Run(() =>
    {
        try
        {
            using var com = new ComObjects(); dynamic service = com.Service(); dynamic folder = com.Own(service.GetFolder("\\"));
            try { dynamic task = com.Own(folder.GetTask(name)); bool enabled = task.Enabled;
                return new State(true, enabled, enabled && ValidDefinition((string)task.Xml, sid, executable, directory)); }
            catch (Exception ex) when (Missing(ex)) { return new State(false, false, false); }
        }
        catch (Exception ex) when (ex is not UserError) { throw Failure(ex); }
    });
    // Repair only an already enabled task. Respect explicit disable/removal in Windows.
    public static async Task RepairEnabled()
    {
        if (!SamePath(InstalledExecutable(), Environment.ProcessPath)) return;
        await RepairEnabled("KotVPN-" + Sid, Sid, Environment.ProcessPath!, AppContext.BaseDirectory);
    }
    internal static async Task RepairEnabled(string name, string sid, string executable, string directory)
    {
        var state = await Inspect(name, sid, executable, directory);
        if (state.Exists && state.Enabled && !state.Healthy) await Set(true, name, sid, executable, directory);
    }
    // Installation-scoped cleanup, including tasks belonging to other users. Never touches their profiles.
    public static Task RemoveOwnedTasks() => Task.Run(() =>
    {
        using var com = new ComObjects(); dynamic service = com.Service(); dynamic folder = com.Own(service.GetFolder("\\"));
        dynamic tasks = com.Own(folder.GetTasks(1));
        var names = new List<string>();
        for (int i = 1; i <= (int)tasks.Count; i++)
        {
            dynamic task = com.Own(tasks.Item(i)); string name = task.Name;
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^KotVPN-S-1-[0-9-]+$")) continue;
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var actions = XDocument.Parse((string)task.Xml).Root?.Element(ns + "Actions")?.Elements().ToArray() ?? [];
            if (actions.Length == 1 && SamePath((string?)actions[0].Element(ns + "Command"), Environment.ProcessPath)) names.Add(name);
        }
        foreach (string name in names) folder.DeleteTask(name, 0);
    });

    // Each operation owns its COM objects on one worker thread, away from the UI.
    internal static Task Set(bool enabled, string name, string sid, string executable, string directory) => Task.Run(() =>
    {
        try
        {
            using var com = new ComObjects();
            dynamic service = com.Service();
            dynamic folder = com.Own(service.GetFolder("\\"));
            if (!enabled)
            {
                try { folder.DeleteTask(name, 0); }
                // COM can map an absent task to FileNotFoundException instead of COMException.
                catch (Exception ex) when (Missing(ex)) { }
                AppLog.Write("startup", "disabled");
                return;
            }

            dynamic definition = com.Own(service.NewTask(0));
            dynamic info = com.Own(definition.RegistrationInfo);
            info.Description = "kot. application based on Xray";
            dynamic principal = com.Own(definition.Principal);
            principal.Id = "Author";
            principal.UserId = sid;
            principal.LogonType = InteractiveToken;
            principal.RunLevel = HighestAvailable;

            dynamic settings = com.Own(definition.Settings);
            settings.Compatibility = 2; // Task Scheduler 2.0, available on all supported Windows versions.
            settings.MultipleInstances = IgnoreNew;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.AllowHardTerminate = false;
            settings.RunOnlyIfIdle = false;
            settings.RunOnlyIfNetworkAvailable = false;
            settings.StartWhenAvailable = true;
            settings.AllowDemandStart = true;
            settings.Enabled = true;
            settings.ExecutionTimeLimit = "PT0S";

            dynamic triggers = com.Own(definition.Triggers);
            dynamic trigger = com.Own(triggers.Create(LogonTrigger));
            trigger.UserId = sid;
            trigger.Enabled = true;

            dynamic actions = com.Own(definition.Actions);
            actions.Context = "Author";
            dynamic action = com.Own(actions.Create(ExecAction));
            // Paths and arguments are separate COM properties; Unicode and spaces need no shell quoting.
            action.Path = executable;
            action.Arguments = "--startup";
            action.WorkingDirectory = directory;
            dynamic registered = com.Own(folder.RegisterTaskDefinition(name, definition, CreateOrUpdate, sid, null, InteractiveToken, null));
            if (!registered.Enabled) throw new UserError("Windows создала задачу автозапуска, но оставила её отключённой.");
            AppLog.Write("startup", "enabled");
        }
        catch (Exception ex) when (ex is not UserError) { throw Failure(ex); }
    });

    internal static Task<bool> IsEnabled(string name) => Task.Run(() =>
    {
        try
        {
            using var com = new ComObjects();
            dynamic service = com.Service();
            dynamic folder = com.Own(service.GetFolder("\\"));
            try { dynamic task = com.Own(folder.GetTask(name)); return (bool)task.Enabled; }
            catch (Exception ex) when (Missing(ex)) { return false; }
        }
        catch (Exception ex) when (ex is not UserError) { throw Failure(ex); }
    });

    static UserError Failure(Exception ex)
    {
        AppLog.Error("startup", ex);
        string reason = ex.HResult switch
        {
            unchecked((int)0x80070005) => "Windows отказала в доступе. Запустите kot. от имени администратора.",
            unchecked((int)0x80070422) or unchecked((int)0x80041315) or unchecked((int)0x800706BA) => "Служба «Планировщик заданий» отключена или недоступна.",
            _ => "Подробности сохранены в логе приложения."
        };
        return new UserError($"Не удалось изменить автозапуск. {reason} Код Windows: 0x{ex.HResult:X8}.");
    }

    sealed class ComObjects : IDisposable
    {
        readonly List<object> objects = [];
        public dynamic Own(object value) { objects.Add(value); return value; }
        public dynamic Service()
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new UserError("Планировщик заданий Windows недоступен.");
            dynamic service = Own(Activator.CreateInstance(type)!);
            service.Connect();
            return service;
        }
        public void Dispose()
        {
            for (int i = objects.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(objects[i])) Marshal.FinalReleaseComObject(objects[i]);
        }
    }
}
