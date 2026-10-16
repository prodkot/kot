using System.Net;
namespace Kot.Core;

// Application downloads use the active core explicitly. Never fall back to a
// direct connection if that core disappears while a request is in flight.
public static class NetworkRequests
{
    public static HttpClientHandler Handler(int proxyPort = 0)
    {
        if (proxyPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(proxyPort));
        return new()
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All,
            UseProxy = proxyPort != 0,
            Proxy = proxyPort == 0 ? null : new WebProxy(new Uri("http://127.0.0.1:" + proxyPort))
        };
    }
}
