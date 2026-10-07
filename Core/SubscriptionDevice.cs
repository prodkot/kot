using System.Security.Cryptography;
using System.Text;

namespace Kot.Core;

public sealed record SubscriptionDevice(string Hwid, string Os, string OsVersion, string Model)
{
    // App-specific, opaque ID. The raw Windows identifier never leaves the PC.
    public static string HashMachineId(string machineId) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("KotVPN.HWID.v1\n" + machineId.Trim().ToLowerInvariant())));

    public void Validate()
    {
        if (Hwid.Length is < 10 or > 64 || !Hwid.All(c => char.IsAsciiLetterOrDigit(c) || c is '=' or '-'))
            throw new UserError("Некорректный HWID устройства.");
        foreach (string value in new[] { Os, OsVersion, Model })
            if (value.Length > 128 || value.Any(c => c < ' ' || c > '~'))
                throw new UserError("Некорректные данные устройства.");
    }
}
