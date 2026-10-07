using Kot.Core;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace Kot.Windows;

public static class DeviceIdentity
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KotVPN.device.v1");
    static string? cached;

    public static SubscriptionDevice Get()
    {
        if (cached == null)
        {
            string path = Path.Combine(Store.Folder, "device.bin");
            if (File.Exists(path))
            {
                byte[]? plain = null;
                try
                {
                    plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                    string id = Encoding.ASCII.GetString(plain);
                    new SubscriptionDevice(id, "Windows", "", "PC").Validate();
                    cached = id;
                }
                catch { throw new UserError("Не удалось прочитать HWID. Сохранённый device.bin оставлен без изменений."); }
                finally { if (plain != null) CryptographicOperations.ZeroMemory(plain); }
            }
            else
            {
                string? machineId = null;
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                    if (key?.GetValue("MachineGuid") is string value && Guid.TryParse(value, out var guid))
                        machineId = guid.ToString("D");
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                // If MachineGuid is inaccessible, create and persist one random ID, never one per request.
                string id = machineId != null ? SubscriptionDevice.HashMachineId(machineId) : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                byte[] plain = Encoding.ASCII.GetBytes(id);
                byte[] encrypted;
                try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                Directory.CreateDirectory(Store.Folder);
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, encrypted); File.Move(temp, path, false);
                cached = id;
            }
        }
        AppLog.Secret(cached);
        return new SubscriptionDevice(cached, "Windows", Environment.OSVersion.Version.ToString(), ClientIdentity.DeviceName);
    }
}
