using Kot.Core;
namespace Kot.Windows;
public static class AppLog
{
    static readonly RedactedSessionLog journal = new();
    public static void Register(Profile profile)
    {
        HashSet<string> secrets = new(StringComparer.OrdinalIgnoreCase);
        foreach (var s in profile.Subscriptions) if (s.Address.Length > 0) secrets.Add(s.Address);
        if (profile.Address.Length > 0) secrets.Add(profile.Address);
        foreach (var routing in profile.RoutingProfiles)
        {
            if (routing.SourceUrl.Length > 0) secrets.Add(routing.SourceUrl);
            foreach (var value in routing.Rules.DirectDomains.Concat(routing.Rules.ProxyDomains).Concat(routing.Rules.BlockDomains).Concat(routing.Rules.DirectIps).Concat(routing.Rules.ProxyIps).Concat(routing.Rules.BlockIps))
                if (value.Length > 0) secrets.Add(value);
        }
        journal.Register(profile.Subscriptions.SelectMany(s => s.Nodes).Concat(profile.Nodes),
            secrets.Concat(new[] { Store.Folder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }).ToArray());
    }
    public static string Clean(string raw) => journal.Clean(raw);
    public static void Write(string source, string message) => journal.Write(source, message);
    public static void Error(string source, Exception error) => journal.Error(source, error);
    public static string Tail() => journal.Tail();
    public static string Snapshot() => journal.Snapshot();
}
