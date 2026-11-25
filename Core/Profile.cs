namespace Kot.Core;
public sealed class Profile
{
    public string Name { get; set; } = "Подписка";
    public string Address { get; set; } = "";
    public bool Local { get; set; }
    public SubscriptionUsage? SubscriptionUsage { get; set; }
    public List<Node> Nodes { get; set; } = [];
    public string Selected { get; set; } = "";
    public string Theme { get; set; } = "dark";
    // Legacy routing preference remains independent from the connection method.
    public string Mode { get; set; } = "all";
    public string ConnectionMode { get; set; } = "tun";
    public string[] Bypass { get; set; } = [];
    public List<RoutingProfile> RoutingProfiles { get; set; } = [];
    public string ActiveRoutingProfile { get; set; } = "";
    public GeoIpOptions GeoIp { get; set; } = new();
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
    public ServiceOptions Services { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public SavedSubscription? CurrentSubscription => Subscriptions.FirstOrDefault(s => s.Id == ActiveSubscription);

    [System.Text.Json.Serialization.JsonIgnore]
    public Node? ManualConnectionNode
    {
        get
        {
            if (Selected == "auto") return null;
            string id = CurrentSubscription?.ResumeNodeId ?? Selected;
            return Nodes.FirstOrDefault(n => n.Id == id);
        }
    }

    public void Normalize()
    {
        ConnectionMode ??= "tun"; DeferredUpdateVersion ??= "";
        Name ??= "Подписка"; Address ??= ""; Selected ??= ""; ActiveSubscription ??= "";
        Subscriptions ??= []; Nodes ??= []; Favorites ??= []; Automation ??= new(); Ping ??= new(); Bypass ??= [];
        Bypass = Configuration.Domains(string.Join('\n', Bypass));
        RoutingProfiles ??= []; ActiveRoutingProfile ??= "";
        GeoIp ??= new(); GeoIp.Validate(); Services ??= new(); Services.Validate();
        if (RoutingProfiles.Count > 20 || RoutingProfiles.Any(p => p == null) || RoutingProfiles.Select(p => p.Id).Distinct().Count() != RoutingProfiles.Count || ActiveRoutingProfile.Length > 0 && RoutingProfiles.All(p => p.Id != ActiveRoutingProfile))
            throw new UserError("Некорректный список профилей маршрутизации. Максимум 20 профилей.");
        foreach (var routing in RoutingProfiles) routing.Validate();
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
        else if (Selected != "auto" && Nodes.All(n => n.Id != Selected)) Selected = Nodes.FirstOrDefault()?.Id ?? "";
    }
    public void CaptureActive()
    {
        if (Address.Length == 0 && !Local) return;
        if (ActiveSubscription.Length == 0) ActiveSubscription = Guid.NewGuid().ToString("N");
        var saved = Subscriptions.FirstOrDefault(s => s.Id == ActiveSubscription);
        if (saved == null) { saved = new() { Id = ActiveSubscription }; Subscriptions.Add(saved); }
        saved.Name = Name; saved.Address = Address; saved.Local = Local; saved.Usage = SubscriptionUsage; saved.Nodes = Nodes.ToList(); saved.Selected = Selected; saved.Updated = Updated; saved.Favorites = Favorites.ToList();
    }
    public void Activate(string id)
    {
        var saved = Subscriptions.FirstOrDefault(s => s.Id == id) ?? throw new UserError("Подписка уже удалена.");
        ActiveSubscription = id; Name = saved.Name; Address = saved.Address; Local = saved.Local; SubscriptionUsage = saved.Usage; Nodes = saved.Nodes.ToList(); Selected = saved.Selected; Updated = saved.Updated; Favorites = saved.Favorites.ToList();
        if (Selected != "auto" && saved.ResumeNodeId == null && Nodes.All(n => n.Id != Selected)) Selected = Nodes.FirstOrDefault()?.Id ?? "";
        Favorites = Favorites.Where(id => Nodes.Any(n => n.Id == id)).Distinct().ToList();
    }
    public void Validate()
    {
        Normalize(); Automation.Validate(); Ping.Validate();
        if (Subscriptions.Count == 0 && Nodes.Count > 0) throw new UserError("Серверы без подписки в резервной копии.");
        if (Subscriptions.Count > 50) throw new UserError("Максимум 50 подписок.");
        if (Subscriptions.Select(s => s.Id).Distinct().Count() != Subscriptions.Count) throw new UserError("Повторяющиеся ID подписок.");
        foreach (var s in Subscriptions)
        {
            bool addressValid = s.Local ? s.Address.Length == 0 : Uri.TryCreate(s.Address, UriKind.Absolute, out var url) && url.Scheme == "https" && url.UserInfo.Length == 0;
            if (new[] { s.MissingEndpointFingerprint, s.MissingTechnicalFingerprint, s.MissingRealityFingerprint }.Any(fingerprint =>
                fingerprint != null && (s.ResumeNodeId == null || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))))
                throw new UserError("Некорректные данные сохранённого сервера подключения.");
            if (!addressValid || s.Usage != null && !s.Usage.Valid() || s.Name.Length > 60 || s.Nodes.Count > 2000 || string.IsNullOrEmpty(s.Id))
                throw new UserError("Некорректная резервная копия подписок.");
            foreach (var node in s.Nodes)
            {
                if (node.Outbound == null || node.Name == null || node.Name.Length > 512 || node.Id == null || node.Id.Length != 64 || node.Id.Any(c => !Uri.IsHexDigit(c)) ||
                    node.SourceId is { } source && (source.Length != 64 || !source.All(Uri.IsHexDigit)) || node.Protocol is not ("vless" or "vmess" or "trojan" or "shadowsocks" or "hysteria2" or "xray") || node.Outbound["type"]?.ToString() != node.Protocol)
                    throw new UserError("Неподдерживаемый сервер в резервной копии.");
                RejectFiles(node.Outbound);
                NodeSecurity.Validate(node);
            }
        }
        if (Theme is not ("dark" or "light") || Mode is not ("all" or "smart") || ConnectionMode is not ("tun" or "proxy")) throw new UserError("Некорректные настройки интерфейса.");
    }
    public void EditSubscription(string id, string name, string address)
    {
        var saved = Subscriptions.FirstOrDefault(s => s.Id == id) ?? throw new UserError("Подписка уже удалена.");
        name = name.Trim(); address = address.Trim();
        if (name.Length is < 1 or > 60) throw new UserError("Название: от 1 до 60 символов.");
        if (saved.Local && address.Length > 0) throw new UserError("У локального профиля нет ссылки на подписку.");
        if (!saved.Local && (address.Length > 2048 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0))
            throw new UserError("Нужна HTTPS-ссылка без логина, пароля и фрагмента.");
        if (!saved.Local && Subscriptions.Any(s => s.Id != id && !s.Local && s.Address == address))
            throw new UserError("Эта ссылка уже есть в другой подписке.");
        CaptureActive();
        if (saved.Address != address) { saved.Usage = null; saved.Updated = null; saved.ExpiryNotice = ""; }
        saved.Name = name; saved.Address = address;
        if (id == ActiveSubscription) Activate(id);
    }
    public void RemoveSubscription(string id)
    {
        if (!Subscriptions.Any(s => s.Id == id)) throw new UserError("Подписка уже удалена.");
        CaptureActive(); Subscriptions.RemoveAll(s => s.Id == id);
        if (id != ActiveSubscription) return;
        ActiveSubscription = ""; Address = ""; Local = false; SubscriptionUsage = null; Nodes = []; Selected = ""; Name = "Подписка"; Updated = null; Favorites = [];
        if (Subscriptions.Count > 0) Activate(Subscriptions[0].Id);
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
    public bool Local { get; set; }
    public SubscriptionUsage? Usage { get; set; }
    public string ExpiryNotice { get; set; } = "";
    public List<Node> Nodes { get; set; } = [];
    public string Selected { get; set; } = "";
    public List<string> Favorites { get; set; } = [];
    public DateTimeOffset? Updated { get; set; }

    // The running endpoint can differ from the UI choice or disappear from the catalog.
    // Keep its identity across retries until the user chooses a new connection.
    public string? ResumeNodeId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? MissingEndpointFingerprint { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? MissingTechnicalFingerprint { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? MissingRealityFingerprint { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RequiresConnectionConfirmation => ResumeNodeId != null && Nodes.All(n => n.Id != ResumeNodeId);
    public const string ConfirmConnection = "Для следующего подключения выберите сервер.";
    public string? Refresh(SubscriptionResult result, DateTimeOffset now, Node? connectionNode = null,
        Action<string>? diagnostics = null)
    {
        if (result.Nodes.Count == 0) throw new UserError("В подписке нет доступных серверов.");
        string? resumeId = connectionNode?.Id ?? ResumeNodeId;
        if (resumeId == null && Selected != "auto" && Selected.Length > 0) resumeId = Selected;
        var catalogNode = Nodes.FirstOrDefault(n => n.Id == resumeId);
        var endpoint = catalogNode ?? connectionNode;
        bool absent = resumeId != null && catalogNode == null;
        string? fingerprint = resumeId == ResumeNodeId ? MissingEndpointFingerprint : null;
        string? technical = resumeId == ResumeNodeId ? MissingTechnicalFingerprint : null;
        string? reality = resumeId == ResumeNodeId ? MissingRealityFingerprint : null;
        if (absent && fingerprint == null && endpoint != null)
            fingerprint = SubscriptionNodes.EndpointFingerprint(endpoint);
        var nodes = SubscriptionNodes.Match(Nodes, result.Nodes, absent ? resumeId : null,
            fingerprint, technical, reality, out int rotations, out int realityMatches);
        bool alreadyMissing = RequiresConnectionConfirmation;
        var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        bool present = resumeId != null && ids.Contains(resumeId);
        string? notice = null;
        if (present && Selected != "auto" && !ids.Contains(Selected)) Selected = resumeId!;
        else if (Selected.Length == 0) Selected = nodes[0].Id;
        if (resumeId != null)
        {
            ResumeNodeId = present && (Selected == "auto" || Selected == resumeId) ? null : resumeId;
            MissingEndpointFingerprint = present ? null : fingerprint ??
                (endpoint == null ? null : SubscriptionNodes.EndpointFingerprint(endpoint));
            // Store a credential-independent fingerprint only if the last catalog
            // had one owner. Later refreshes must not turn an ambiguity into proof.
            if (!present && catalogNode != null)
            {
                technical = SubscriptionNodes.TechnicalFingerprint(catalogNode);
                if (technical != null && Nodes.Count(n => SubscriptionNodes.TechnicalFingerprint(n) == technical) != 1)
                    technical = null;
                reality = SubscriptionNodes.UniqueRealityFingerprint(catalogNode, Nodes);
            }
            MissingTechnicalFingerprint = present ? null : technical;
            MissingRealityFingerprint = present ? null : reality;
            if (!present && !alreadyMissing)
                notice = $"В подписке «{Name}» не удалось распознать сервер «{endpoint?.Name ?? "ранее выбранный"}». " + ConfirmConnection;
        }
        if (diagnostics != null)
        {
            var reason = resumeId == null || present ? "available" : technical != null &&
                nodes.Count(n => SubscriptionNodes.TechnicalFingerprint(n) == technical) > 1 ? "ambiguous" : "no-safe-match";
            if (!present && reality != null && result.Nodes.Count(n => SubscriptionNodes.RealityFingerprint(n) == reality) > 1)
                reason = "ambiguous";
            diagnostics($"credential-rotations={rotations}, reality-matches={realityMatches}, target={reason}");
        }
        Nodes = nodes;
        Favorites.RemoveAll(id => !ids.Contains(id));
        Usage = result.Usage;
        Updated = now;
        return notice;
    }
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
    bool established;
    public void Start(DateTimeOffset now) { established = false; Desired = true; ResetDelay(now); }
    public void ResetDelay(DateTimeOffset now) { Failures = 0; Next = now; }
    public void Stop() { established = false; Desired = false; Failures = 0; Next = DateTimeOffset.MaxValue; }
    public void Healthy() { established = true; Failures = 0; }
    public void Retry(DateTimeOffset now)
    {
        if (!Desired) return;
        Failures = Math.Min(Failures + 1, 10);
        // A server that never connected must eventually expose its error instead
        // of spinning forever. Established sessions still recover automatically.
        if (!established && Failures >= 3) { Desired = false; Next = DateTimeOffset.MaxValue; return; }
        int[] delays = [2, 5, 10, 20, 30, 60]; Next = now.AddSeconds(delays[Math.Min(Failures - 1, delays.Length - 1)]);
    }
    public bool Due(DateTimeOffset now, bool online) => Desired && online && now >= Next;
}
