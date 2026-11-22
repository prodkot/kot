namespace Kot.Core;

public static class ClientIdentity
{
    public static string Version => typeof(ClientIdentity).Assembly.GetName().Version!.ToString(3);
    static string? Metadata(string key) => typeof(ClientIdentity).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
    public static string BuildNumber => Metadata("KotBuildId") ?? "local";
    public static string DisplayVersion => typeof(ClientIdentity).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
    public static string Build
    {
        get
        {
            string revision = Metadata("KotSourceCommit") ?? typeof(ClientIdentity).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+').LastOrDefault() ?? "";
            return revision.Length >= 12 && revision.Take(12).All(Uri.IsHexDigit) ? revision[..12] : "unknown";
        }
    }
    public static string DeviceName => $"kot. windows ({Version})";
    // Keep the compatibility token so subscription panels return supported link formats.
    public static string SubscriptionUserAgent => DeviceName + " v2rayN/7.0";
}
