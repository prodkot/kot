namespace Kot.Core;

public static class ClientIdentity
{
    public const string Version = "0.5.7";
    public static string DeviceName => $"kot. windows ({Version})";
    // Keep the compatibility token so subscription panels return supported link formats.
    public static string SubscriptionUserAgent => DeviceName + " v2rayN/7.0";
}
