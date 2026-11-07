using Kot.Windows;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

internal static class ServiceIntegrityChecks
{
    internal static int CurrentLevel()
    {
        using var identity = WindowsIdentity.GetCurrent();
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (!GetTokenInformation(identity.AccessToken.DangerousGetHandle(), 25, buffer, 256, out _)) throw new System.ComponentModel.Win32Exception();
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
            return int.Parse(sid.Value[(sid.Value.LastIndexOf('-') + 1)..]);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void Fixture(string temp)
    {
        Console.WriteLine("integrity=" + CurrentLevel());
        string probe = Path.Combine(temp, Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(probe); File.WriteAllText(Path.Combine(probe, "probe"), "ok"); Console.WriteLine("temp=writable"); }
        catch (UnauthorizedAccessException) { Console.WriteLine("temp=denied"); }
        finally { if (Directory.Exists(probe)) { File.Delete(Path.Combine(probe, "probe")); Directory.Delete(probe); } }
    }

    internal static async Task Run(string exe, string folder, Action<bool, string> check)
    {
        string root = Path.Combine(folder, "integrity-services"), service = Path.Combine(root, "telegram"), package = Path.Combine(service, "packages", "fixture");
        Directory.CreateDirectory(package);
        foreach (string file in Directory.GetFiles(Path.GetDirectoryName(exe)!)) File.Copy(file, Path.Combine(package, Path.GetFileName(file)));
        string fixture = Path.Combine(package, Path.GetFileName(exe)), temp = Path.Combine(folder, "medium-temp");
        Directory.CreateDirectory(temp); ServiceIntegrity.SetLabel(temp, ServiceIntegrity.Medium, true);
        ServiceIntegrity.SetLabel(service, 0x1000, true);
        ServiceIntegrity.SetLabel(fixture, 0x1000, false, 7); // Keep no-read-up/no-execute-up restrictions, too.
        string dacl = new FileInfo(fixture).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        async Task<string> Launch()
        {
            using var process = new NativeCore(fixture, ["--service-integrity", temp], package, "integrity fixture");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            check(process.ExitCode == 0, "integrity fixture exits normally");
            return await process.ReadRecentOutput(true);
        }
        string before = await Launch();
        check(before.Contains("integrity=4096") && before.Contains("temp=denied"), "a low-labeled executable reproduces the child token downgrade and temporary-directory failure");
        check(ServiceIntegrity.Prepare(root, service, ServiceIntegrity.Medium), "owned low-integrity service files are repaired automatically");
        string after = await Launch();
        check(after.Contains("integrity=8192") && after.Contains("temp=writable"), "a repaired proxy launches at medium integrity and creates temporary files");
        check(new FileInfo(fixture).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group) == dacl,
            "integrity repair preserves package ownership and discretionary access rules");
        check(ServiceIntegrity.ReadPolicy(fixture) == 7, "integrity repair retains existing mandatory read, write and execute restrictions");
        check(!ServiceIntegrity.Prepare(root, service, ServiceIntegrity.Medium), "repeating integrity preparation leaves correct labels unchanged");

        string outside = Path.Combine(folder, "outside-service"); Directory.CreateDirectory(outside); ServiceIntegrity.SetLabel(outside, 0x1000, true);
        bool rejected = false;
        try { ServiceIntegrity.Prepare(root, outside, ServiceIntegrity.High); } catch (Kot.Core.UserError) { rejected = true; }
        check(rejected && ServiceIntegrity.ReadLabel(outside) == 0x1000, "repair rejects another application's directory without changing its label");
        string link = Path.Combine(service, "redirected");
        try
        {
            Directory.CreateSymbolicLink(link, outside); ServiceIntegrity.SetLabel(service, 0x1000, false);
            rejected = false;
            try { ServiceIntegrity.Prepare(root, service, ServiceIntegrity.Medium); } catch (Kot.Core.UserError) { rejected = true; }
            check(rejected && ServiceIntegrity.ReadLabel(service) == 0x1000 && ServiceIntegrity.ReadLabel(outside) == 0x1000,
                "a redirected child rejects the complete repair before any label changes");
        }
        catch (UnauthorizedAccessException) { Console.WriteLine("SKIP integrity symlink fixture: symbolic-link privilege required"); }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }

        if (CurrentLevel() >= ServiceIntegrity.High)
        {
            check(ServiceIntegrity.Prepare(root, service, ServiceIntegrity.High), "zapret preparation repairs both low and medium labels to high integrity");
            check((await Launch()).Contains("integrity=12288"), "an elevated host keeps high integrity in a repaired WinDivert worker");
            check(!ServiceIntegrity.Prepare(root, service, ServiceIntegrity.Medium) && ServiceIntegrity.ReadLabel(fixture) == ServiceIntegrity.High,
                "preparation never lowers an existing higher-integrity label");
        }
        else Console.WriteLine("SKIP high-integrity worker fixture: elevated test host required (covered in Windows CI)");
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int information, IntPtr buffer, int size, out int returned);
}
