using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;
using Kot.Core;
using System.Runtime.InteropServices;
namespace Kot.Windows;
public static class Startup
{
    static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    static string TaskName => "KotVPN-" + Sid;
    public static async Task Set(bool enabled)
    {
        if (!enabled)
        {
            object? service = null, folder = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
                ((dynamic)service!).Connect(); folder = ((dynamic)service).GetFolder("\\");
                ((dynamic)folder).DeleteTask(TaskName, 0); return;
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { return; } // Already absent.
            finally { if (folder != null) Marshal.FinalReleaseComObject(folder); if (service != null) Marshal.FinalReleaseComObject(service); }
        }
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        string temp = Path.Combine(Store.Folder, "startup.xml");
        try
        {
            if (enabled)
            {
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                XElement E(string name, params object[] content) => new(ns + name, content);
                var task = E("Task", new XAttribute("version", "1.2"),
                    E("RegistrationInfo", E("Description", "kot. application based on sing-box")),
                    E("Triggers", E("LogonTrigger", E("Enabled", "true"), E("UserId", Sid))),
                    E("Principals", E("Principal", new XAttribute("id", "Author"), E("UserId", Sid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
                    E("Settings", E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"), E("AllowHardTerminate", "false"), E("StartWhenAvailable", "false"), E("AllowStartOnDemand", "true"), E("Enabled", "true"), E("ExecutionTimeLimit", "PT0S")),
                    E("Actions", new XAttribute("Context", "Author"), E("Exec", E("Command", Environment.ProcessPath!), E("Arguments", "--startup"), E("WorkingDirectory", AppContext.BaseDirectory))));
                new XDocument(new XDeclaration("1.0", "utf-8", null), task).Save(temp);
                foreach (string arg in new[] { "/Create", "/TN", TaskName, "/XML", temp, "/F" }) psi.ArgumentList.Add(arg);
            }
            else foreach (string arg in new[] { "/Delete", "/TN", TaskName, "/F" }) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
            try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); } catch (TimeoutException) { if (!p.HasExited) p.Kill(); throw new UserError("Планировщик заданий не ответил вовремя."); } await Task.WhenAll(output, error);
            if (p.ExitCode != 0) throw new UserError("Не удалось изменить автозапуск в Планировщике заданий Windows.");
        }
        finally { try { File.Delete(temp); } catch { } }
    }
}
