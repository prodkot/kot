using System.Net;
namespace Kot.Core;

// Application downloads use the active core explicitly. Never fall back to a
// direct connection if that core disappears while a request is in flight.
public static class NetworkRequests
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> proxySecrets = new();
    // Credentials belong to the lifetime of one reserved loopback listener.
    // Existing handlers keep their credentials if that listener is stopped;
    // they cannot silently retry directly or adopt a later session's key.
    public static IDisposable RegisterProxy(int port, string secret)
    {
        if (port is < 1 or > 65535 || secret.Length < 32) throw new ArgumentException("Invalid private proxy credentials.");
        if (!proxySecrets.TryAdd(port, secret)) throw new InvalidOperationException("Proxy port is already registered.");
        return new ProxyRegistration(port, secret);
    }
    sealed class ProxyRegistration(int port, string secret) : IDisposable
    {
        public void Dispose() => ((ICollection<KeyValuePair<int, string>>)proxySecrets).Remove(new(port, secret));
    }
    public static HttpClientHandler Handler(int proxyPort = 0)
    {
        if (proxyPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(proxyPort));
        return new()
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All,
            UseProxy = proxyPort != 0,
            Proxy = proxyPort == 0 ? null : new WebProxy(new Uri("http://127.0.0.1:" + proxyPort))
            { Credentials = proxySecrets.TryGetValue(proxyPort, out var secret) ? new NetworkCredential("kot", secret) : null }
        };
    }
}
