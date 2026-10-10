using Kot.Windows;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

internal static class StartupFixture
{
    public static void Run(Action<bool, string> check)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User!.Value, name = "KotVPN-test-" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "kot тест & spaces " + Guid.NewGuid().ToString("N"));
        string executable = Path.Combine(directory, "Кот приложение.exe");
        Directory.CreateDirectory(directory);
        object? service = null, folder = null, task = null;
        Task Set(bool enabled, string path) => Startup.Set(enabled, name, sid, path, directory);
        try
        {
            check(!Startup.IsEnabled(name).GetAwaiter().GetResult(), "absent startup task is reported as disabled");
            Set(false, executable).GetAwaiter().GetResult(); Set(false, executable).GetAwaiter().GetResult();
            check(!Startup.IsEnabled(name).GetAwaiter().GetResult(), "disabling an absent startup task is idempotent");
            Set(true, executable).GetAwaiter().GetResult();
            check(Startup.IsEnabled(name).GetAwaiter().GetResult(), "startup registration succeeds in the real Windows Task Scheduler");
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
            ((dynamic)service!).Connect(); folder = ((dynamic)service).GetFolder("\\");
            task = ((dynamic)folder).GetTask(name);
            var xml = XDocument.Parse((string)((dynamic)task).Xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var root = xml.Root!;
            var trigger = root.Element(ns + "Triggers")!.Element(ns + "LogonTrigger")!;
            var principal = root.Element(ns + "Principals")!.Element(ns + "Principal")!;
            var action = root.Element(ns + "Actions")!.Element(ns + "Exec")!;
            check((string?)trigger.Element(ns + "UserId") == sid && (string?)principal.Element(ns + "UserId") == sid,
                "startup trigger and principal belong to the current user");
            check((string?)principal.Element(ns + "LogonType") == "InteractiveToken" && (string?)principal.Element(ns + "RunLevel") == "HighestAvailable",
                "startup uses the interactive desktop with administrator privileges and no saved password");
            check((string?)action.Element(ns + "Command") == executable && (string?)action.Element(ns + "WorkingDirectory") == directory
                && (string?)action.Element(ns + "Arguments") == "--startup", "Unicode, ampersands and spaces survive startup registration");
            var settings = root.Element(ns + "Settings")!;
            check((string?)settings.Element(ns + "DisallowStartIfOnBatteries") == "false" && (string?)settings.Element(ns + "StopIfGoingOnBatteries") == "false"
                && (string?)settings.Element(ns + "ExecutionTimeLimit") == "PT0S", "startup is allowed on batteries without a runtime limit");
            ((dynamic)task).Enabled = false;
            check(!Startup.IsEnabled(name).GetAwaiter().GetResult(), "externally disabled startup task is detected");
            string moved = Path.Combine(directory, "Другой кот.exe");
            Set(true, moved).GetAwaiter().GetResult(); Set(true, moved).GetAwaiter().GetResult();
            Marshal.FinalReleaseComObject(task); task = ((dynamic)folder).GetTask(name);
            xml = XDocument.Parse((string)((dynamic)task).Xml);
            check(Startup.IsEnabled(name).GetAwaiter().GetResult()
                && (string?)xml.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Command") == moved,
                "repeated enable repairs an existing task and updates its executable path");
            Set(false, moved).GetAwaiter().GetResult(); Set(false, moved).GetAwaiter().GetResult();
            check(!Startup.IsEnabled(name).GetAwaiter().GetResult(), "registered startup task is removed and repeated removal succeeds");
        }
        finally
        {
            try { Set(false, executable).GetAwaiter().GetResult(); }
            finally
            {
                if (task != null) Marshal.FinalReleaseComObject(task);
                if (folder != null) Marshal.FinalReleaseComObject(folder);
                if (service != null) Marshal.FinalReleaseComObject(service);
                Directory.Delete(directory, true);
            }
        }
    }
}
