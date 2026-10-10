using System.Runtime.InteropServices;
using System.Security.Principal;
using Kot.Core;

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
        string sid = Sid;
        return Set(enabled, "KotVPN-" + sid, sid, Environment.ProcessPath!, AppContext.BaseDirectory);
    }

    public static Task<bool> IsEnabled() => IsEnabled("KotVPN-" + Sid);

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
            info.Description = "kot. application based on sing-box";
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
