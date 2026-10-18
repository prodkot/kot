namespace Kot.Core;

public static class ClientIdentity
{
    public const string Version = "0.8.0";
    public static string Build
    {
        get
        {
            string revision = typeof(ClientIdentity).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+').LastOrDefault() ?? "";
            return revision.Length >= 12 && revision.Take(12).All(Uri.IsHexDigit) ? revision[..12] : "unknown";
        }
    }
    public static string DeviceName => $"kot. windows ({Version})";
    // Keep the compatibility token so subscription panels return supported link formats.
    public static string SubscriptionUserAgent => DeviceName + " v2rayN/7.0";
}
