using Kot.Core;
namespace Kot.Windows;
public static class AppLog
{
    static readonly SessionLog journal = new();
    static readonly object sync = new();
    static readonly List<Node> nodes = [];
    static readonly HashSet<string> secrets = new(StringComparer.OrdinalIgnoreCase);
    public static void Register(Profile profile)
    {
        lock (sync)
        {
            secrets.Add(profile.Services.Telegram.Secret);
            secrets.Add("dd" + profile.Services.Telegram.Secret);
            foreach (var node in profile.Subscriptions.SelectMany(s => s.Nodes).Concat(profile.Nodes))
                if (!nodes.Any(old => old.Id == node.Id && System.Text.Json.Nodes.JsonNode.DeepEquals(old.Outbound, node.Outbound))) nodes.Add(node);
            foreach (var s in profile.Subscriptions) if (s.Address.Length > 0) secrets.Add(s.Address);
            if (profile.Address.Length > 0) secrets.Add(profile.Address);
            foreach (var routing in profile.RoutingProfiles)
            {
                if (routing.SourceUrl.Length > 0) secrets.Add(routing.SourceUrl);
                foreach (var value in routing.Rules.DirectDomains.Concat(routing.Rules.ProxyDomains).Concat(routing.Rules.BlockDomains).Concat(routing.Rules.DirectIps).Concat(routing.Rules.ProxyIps).Concat(routing.Rules.BlockIps))
                    if (value.Length > 0) secrets.Add(value);
            }
        }
    }
    public static void Secret(string text) { if (text.Length > 0) lock (sync) secrets.Add(text); }
    public static string Clean(string raw)
    {
        lock (sync) return CoreDiagnostics.RedactMany(raw, nodes, secrets.Concat(new[] { Store.Folder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }).ToArray());
    }
    public static void Write(string source, string message) => journal.Write(source, message);
    public static void Error(string source, Exception error) => journal.Error(source, error);
    public static string Tail() => Clean(journal.Tail());
    public static string Snapshot() => Clean(journal.Snapshot());
}
