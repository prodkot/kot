namespace Kot.Core;
public sealed class Profile
{
    public string Name { get; set; } = "Подписка";
    public string Address { get; set; } = "";
    public List<Node> Nodes { get; set; } = [];
    public string Selected { get; set; } = "";
    public string Theme { get; set; } = "dark";
    public string Accent { get; set; } = "lime";
    // Legacy routing preference remains independent from the connection method.
    public string Mode { get; set; } = "all";
    public string ConnectionMode { get; set; } = "tun";
    public string[] Bypass { get; set; } = [];
    public bool Startup { get; set; }
    public bool AutoConnect { get; set; }
    public bool Tray { get; set; } = true;
    public bool KillSwitch { get; set; }
    public string DeferredUpdateVersion { get; set; } = "";
    public bool SendHwid { get; set; } = true;
    public PingOptions Ping { get; set; } = new();
    public DateTimeOffset? Updated { get; set; }
    public List<SavedSubscription> Subscriptions { get; set; } = [];
    public string ActiveSubscription { get; set; } = "";
    public List<string> Favorites { get; set; } = [];
    public AutomationOptions Automation { get; set; } = new();
    public void Normalize()
    {
        ConnectionMode ??= "tun"; DeferredUpdateVersion ??= "";
        Name ??= "Подписка"; Address ??= ""; Selected ??= ""; ActiveSubscription ??= "";
        Subscriptions ??= []; Nodes ??= []; Favorites ??= []; Automation ??= new(); Ping ??= new(); Bypass ??= [];
        foreach (var subscription in Subscriptions)
        {
            if (subscription == null) throw new UserError("Некорректная подписка в профиле.");
            subscription.Name ??= "Подписка"; subscription.Address ??= ""; subscription.Selected ??= ""; subscription.Nodes ??= []; subscription.Favorites ??= [];
            if (subscription.Nodes.Any(n => n == null)) throw new UserError("Некорректный сервер в профиле.");
        }
        if (Nodes.Any(n => n == null)) throw new UserError("Некорректный сервер в профиле.");
        if (Subscriptions.Count == 0 && Address.Length > 0)
        {
            ActiveSubscription = Guid.NewGuid().ToString("N");
            CaptureActive();
        }
        if (Subscriptions.Count > 0)
        {
            var active = Subscriptions.FirstOrDefault(s => s.Id == ActiveSubscription) ?? Subscriptions[0];
            Activate(active.Id);
        }
        if (Selected != "auto" && Nodes.All(n => n.Id != Selected)) Selected = Nodes.FirstOrDefault()?.Id ?? "";
    }
    public void CaptureActive()
    {
        if (Address.Length == 0) return;
        if (ActiveSubscription.Length == 0) ActiveSubscription = Guid.NewGuid().ToString("N");
        var saved = Subscriptions.FirstOrDefault(s => s.Id == ActiveSubscription);
        if (saved == null) { saved = new() { Id = ActiveSubscription }; Subscriptions.Add(saved); }
        saved.Name = Name; saved.Address = Address; saved.Nodes = Nodes.ToList(); saved.Selected = Selected; saved.Updated = Updated; saved.Favorites = Favorites.ToList();
    }
    public void Activate(string id)
    {
        var saved = Subscriptions.FirstOrDefault(s => s.Id == id) ?? throw new UserError("Подписка уже удалена.");
        ActiveSubscription = id; Name = saved.Name; Address = saved.Address; Nodes = saved.Nodes.ToList(); Selected = saved.Selected; Updated = saved.Updated; Favorites = saved.Favorites.ToList();
        if (Selected != "auto" && Nodes.All(n => n.Id != Selected)) Selected = Nodes.FirstOrDefault()?.Id ?? "";
        Favorites = Favorites.Where(id => Nodes.Any(n => n.Id == id)).Distinct().ToList();
    }
    public void Validate()
    {
        Normalize(); Automation.Validate(); Ping.Validate(); Configuration.Domains(string.Join('\n', Bypass));
        if (Subscriptions.Count == 0 && Nodes.Count > 0) throw new UserError("Серверы без подписки в резервной копии.");
        if (Subscriptions.Count > 50) throw new UserError("Максимум 50 подписок.");
        if (Subscriptions.Select(s => s.Id).Distinct().Count() != Subscriptions.Count) throw new UserError("Повторяющиеся ID подписок.");
        foreach (var s in Subscriptions)
        {
            if (!Uri.TryCreate(s.Address, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length > 0 || s.Name.Length > 60 || s.Nodes.Count > 2000 || string.IsNullOrEmpty(s.Id))
                throw new UserError("Некорректная резервная копия подписок.");
            foreach (var node in s.Nodes)
            {
                if (node.Outbound == null || node.Name == null || node.Name.Length > 512 || node.Id == null || node.Id.Length != 64 || node.Id.Any(c => !Uri.IsHexDigit(c)) || node.Protocol is not ("vless" or "vmess" or "trojan" or "shadowsocks" or "hysteria2") || node.Outbound["type"]?.ToString() != node.Protocol)
                    throw new UserError("Неподдерживаемый сервер в резервной копии.");
                RejectFiles(node.Outbound);
                NodeSecurity.Validate(node);
            }
        }
        if (Theme is not ("dark" or "light") || Accent is not ("gray" or "lime" or "green" or "purple") || Mode is not ("all" or "smart") || ConnectionMode is not ("tun" or "proxy")) throw new UserError("Некорректные настройки интерфейса.");
    }
    static void RejectFiles(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
            foreach (var item in obj) { if (item.Key.EndsWith("_path", StringComparison.OrdinalIgnoreCase) || item.Key is "plugin" or "plugin_opts" or "detour") throw new UserError("Ссылки на файлы и плагины в профиле запрещены."); RejectFiles(item.Value); }
        else if (node is System.Text.Json.Nodes.JsonArray arr) foreach (var item in arr) RejectFiles(item);
    }
}
public sealed class SavedSubscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Подписка";
    public string Address { get; set; } = "";
    public List<Node> Nodes { get; set; } = [];
    public string Selected { get; set; } = "";
    public List<string> Favorites { get; set; } = [];
    public DateTimeOffset? Updated { get; set; }
}
public sealed class AutomationOptions
{
    public bool Reconnect { get; set; } = true;
    public int RefreshHours { get; set; } = 6;
    public int PingMinutes { get; set; } = 0;
    public int AutoMinutes { get; set; } = 3;
    public int AutoToleranceMs { get; set; } = 50;
    public void Validate()
    {
        if (RefreshHours is < 0 or > 168 || PingMinutes is < 0 or > 120 || AutoMinutes is < 1 or > 60 || AutoToleranceMs is < 0 or > 1000)
            throw new UserError("Некорректные интервалы автоматизации.");
    }
}
public sealed class ReconnectPolicy
{
    public bool Desired { get; private set; }
    public int Failures { get; private set; }
    public DateTimeOffset Next { get; private set; }
    public void Start(DateTimeOffset now) { Desired = true; Failures = 0; Next = now; }
    public void Stop() { Desired = false; Failures = 0; Next = DateTimeOffset.MaxValue; }
    public void Healthy() { Failures = 0; }
    public void Retry(DateTimeOffset now)
    {
        Failures = Math.Min(Failures + 1, 10);
        int[] delays = [2, 5, 10, 20, 30, 60]; Next = now.AddSeconds(delays[Math.Min(Failures - 1, delays.Length - 1)]);
    }
    public bool Due(DateTimeOffset now, bool online) => Desired && online && now >= Next;
}
