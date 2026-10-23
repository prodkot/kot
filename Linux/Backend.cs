using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Kot.Core;
namespace Kot.Linux;

public sealed class Backend
{
    readonly Store store;
    Profile profile;
    readonly GnomeProxy proxy = new();
    readonly GeoIpCache geo = new(Path.Combine(Paths.Cache, "geoip-cidr"));
    readonly Tunnel tunnel;
    readonly SemaphoreSlim gate = new(1);
    readonly object output = new();
    readonly CancellationTokenSource lifetime = new();
    readonly ConcurrentDictionary<string, PingResult> pings = new();
    readonly UsageTracker usage;
    readonly TrafficSampler sampler = new();
    TrafficReading traffic = TrafficReading.Empty;
    CancellationTokenSource? import, ping, tools;
    Task pingTask = Task.CompletedTask, toolsTask = Task.CompletedTask;
    readonly ConcurrentQueue<object> diagnostic = new();
    string toolStatus = "", toolError = "", backgroundError = "";
    string toolKind = "diagnostics", checkAddress = "";
    readonly ConcurrentQueue<int?> qualitySamples = new();
    QualitySummary? quality;
    bool ready, desired, normalExit, established;
    int done, total, retry;
    DateTimeOffset nextRetry, nextRefresh = DateTimeOffset.UtcNow.AddMinutes(1);
    DateTimeOffset nextHealth = DateTimeOffset.UtcNow.AddSeconds(30);
    DateTimeOffset nextRulesCheck = DateTimeOffset.UtcNow.AddMinutes(1);
    readonly Dictionary<string, DateTimeOffset> routingAttempts = [];
    List<string> warnings = [];
    readonly Queue<string> journal = new();
    readonly List<Task> pending = [];
    static readonly JsonSerializerOptions Json = Store.Json;
    string UsagePath => Path.Combine(Paths.Data, "traffic-history.json");
    public Backend(Store store)
    {
        this.store = store; profile = store.Load();
        tunnel = new(proxy, new XrayAssets(Path.Combine(Paths.Cache, "xray-assets")), geo);
        try { usage = new(UsageFile.Load(UsagePath)); } catch { throw new UserError("История трафика повреждена. Старый файл сохранён; переместите его вручную перед запуском."); }
        tunnel.Changed = () => { if (tunnel.State == "connected") { usage.Start(tunnel.Session, tunnel.Started, tunnel.StartedUtc); retry = 0; } Log("Состояние: " + tunnel.State); Snapshot(); };
        tunnel.Ended = (session, counters) =>
        {
            usage.End(session, Tunnel.Clock, DateTimeOffset.UtcNow, counters);
            try { UsageFile.Save(UsagePath, usage.Archive); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or UserError)
            { backgroundError = "История трафика не сохранена; остановка VPN продолжается."; }
        };
    }
    void Send(object value)
    {
        lock (output)
        {
            try { Console.Out.WriteLine(JsonSerializer.Serialize(value, Json)); Console.Out.Flush(); }
            catch (IOException) { ready = false; } // a dead shell must not interrupt network cleanup
        }
    }
    void Log(string text) { journal.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + text); while (journal.Count > 120) journal.Dequeue(); }
    static string Text(JsonElement d, string name, string fallback = "") => d.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
    void Save(Action<Profile> edit) => profile = store.Change(profile, edit);
    Node Node(JsonElement data) => profile.Nodes.FirstOrDefault(n => n.Id == Text(data, "id", profile.Selected)) ?? throw new UserError("Выберите сервер.");
    public async Task Run()
    {
        await proxy.Restore();
        var ticker = Tick();
        try
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
            while (await ReadLine(reader, lifetime.Token) is { } line)
            {
                using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                JsonElement request = doc.RootElement.Clone();
                if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("id", out var id) || !id.TryGetInt32(out _) || Text(request, "action").Length > 64 || !request.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) throw new UserError("Неверный запрос оболочки.");
                string action = Text(request, "action");
                if (action is "cancelImport" or "cancelPing" or "cancelTools" || action == "toggle" && tunnel.State == "connecting")
                {
                    if (action == "cancelImport") import?.Cancel();
                    if (action == "cancelPing") ping?.Cancel();
                    if (action == "cancelTools") tools?.Cancel();
                    if (action == "toggle") { desired = false; tunnel.Cancel(); }
                    Send(new { kind = "reply", id = id.GetInt32(), ok = true }); continue;
                }
                if (action == "shutdown") { normalExit = true; break; }
                pending.RemoveAll(t => t.IsCompleted);
                if (pending.Count >= 32) throw new UserError("Слишком много запросов.");
                pending.Add(Handle(request));
            }
        }
        finally
        {
            desired = false; lifetime.Cancel(); import?.Cancel(); ping?.Cancel(); tools?.Cancel(); tunnel.Cancel();
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(20)); } catch { }
            await tunnel.Stop(normalExit); await Task.WhenAll(pingTask, toolsTask);
            try { await ticker; } catch (OperationCanceledException) { }
        }
    }
    static async Task<string?> ReadLine(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder(); char[] buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0)
        { if (buffer[0] == '\n') return text.ToString(); if (text.Length >= 32 * 1024 * 1024) throw new UserError("Запрос слишком большой."); text.Append(buffer[0]); }
        return text.Length == 0 ? null : throw new UserError("Неполный запрос.");
    }
    async Task Handle(JsonElement request)
    {
        int id = request.GetProperty("id").GetInt32();
        await gate.WaitAsync();
        try
        {
            object? result = await Action(Text(request, "action"), request.GetProperty("data"));
            Snapshot(); Send(new { kind = "reply", id, ok = true, data = result });
        }
        catch (Exception ex) { string error = ex is UserError ? ex.Message : ex is OperationCanceledException ? "Операция отменена или истекло время ожидания." : "Операция не завершена: " + ex.GetType().Name; Log("Ошибка операции: " + ex.GetType().Name); Send(new { kind = "reply", id, ok = false, error }); Snapshot(); }
        finally { gate.Release(); }
    }
    async Task<object?> Action(string action, JsonElement data)
    {
        if (action.StartsWith("service", StringComparison.Ordinal))
            throw new UserError("Дополнительные сервисы убраны из Linux-версии.");
        switch (action)
        {
            case "ready": ready = true; desired = profile.AutoConnect; return null;
            case "uiReady": case "heartbeat": return null;
            case "view":
                if (Text(data, "page") is not ("home" or "servers" or "statistics" or "diagnostics" or "logs" or "settings"))
                    throw new UserError("Неизвестная страница.");
                return null;
            case "toggle":
                if (tunnel.State != "idle" || desired) { desired = false; established = false; await tunnel.Stop(); }
                else { desired = true; established = false; await Connect(); }
                return null;
            case "import": case "refresh": case "importText": await Import(action, data); return null;
            case "readSubscription":
                var sub = profile.Subscriptions.Single(s => s.Id == Text(data, "id")); return new { sub.Id, sub.Name, sub.Address, sub.Local };
            case "editSubscription": Save(p => p.EditSubscription(Text(data, "id"), Text(data, "name"), Text(data, "address"))); return null;
            case "switchSubscription": await Restart(() => Save(p => { p.CaptureActive(); p.Activate(Text(data, "id")); })); return null;
            case "remove": await Restart(() => Save(p => p.RemoveSubscription(Text(data, "id")))); return null;
            case "select":
                string selected = Text(data, "id"); if (selected != "auto" && profile.Nodes.All(n => n.Id != selected)) throw new UserError("Сервер удалён.");
                await Restart(() => Save(p => p.Selected = selected)); return null;
            case "favorite": string favorite = Node(data).Id; Save(p => { if (!p.Favorites.Remove(favorite)) p.Favorites.Add(favorite); }); return null;
            case "settings": await Settings(data); return null;
            case "automationSettings":
                var automation = JsonSerializer.Deserialize<AutomationOptions>(data, Json)!; automation.Validate();
                if (automation.PingMinutes != profile.Automation.PingMinutes || automation.AutoMinutes != profile.Automation.AutoMinutes || automation.AutoToleranceMs != profile.Automation.AutoToleranceMs)
                    throw new UserError("Периодические проверки списка и повторный выбор Авто в Linux ещё не перенесены. Доступны ручной пинг, Авто при подключении, обновление подписок и восстановление соединения.");
                Save(p => p.Automation = automation); return null;
            case "pingSettings": var options = JsonSerializer.Deserialize<PingOptions>(data, Json)!; options.Validate(); Save(p => p.Ping = options); return null;
            case "pingAll": case "pingNode":
                if (ping != null) throw new UserError("Проверка уже идёт.");
                ping = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                pingTask = Ping(action == "pingAll" ? profile.Nodes.ToArray() : [Node(data)], ping); return null;
            case "backupExport": return new { native = "save", name = "kot.kotbackup", bytes = Convert.ToBase64String(Backup.Export(profile, Text(data, "password"))) };
            case "backupImport":
                var restored = Backup.Import(Convert.FromBase64String(Text(data, "bytes")), Text(data, "password"));
                await Restart(() => { store.Save(restored); profile = restored; }); return null;
            case "readHwid": return new { hwid = Device().Hwid, name = "Ubuntu Linux" };
            case "copyHwid": return new { native = "copy", text = Device().Hwid };
            case "copyLog": case "diagnostics": case "copyDiagnostics": return new { native = "copy", text = "kot. Linux " + ClientIdentity.Version + "\n" + string.Join('\n', journal) + "\n" + JsonSerializer.Serialize(diagnostic, Json) };
            case "readNodeQr": return Qr(Node(data));
            case "copyNodeLink": return new { native = "copy", text = NodeLinks.Export(Node(data)) };
            case "copyNodeQr": case "saveNodeQr": return new { native = action == "copyNodeQr" ? "copyImage" : "saveImage", name = "kot-server.png", image = QrImage(Node(data)) };
            case "readQrPixels": return new { text = QrCodes.Decode(Convert.FromBase64String(Text(data, "rgb")), data.GetProperty("width").GetInt32(), data.GetProperty("height").GetInt32()) };
            case "routingSelect":
                string rid = Text(data, "id"); if (rid.Length > 0 && profile.RoutingProfiles.All(p => p.Id != rid)) throw new UserError("Профиль удалён.");
                await Restart(() => Save(p => p.ActiveRoutingProfile = rid)); return null;
            case "routingSave": case "routingImport": case "routingRefresh": await PutRouting(action, data); return null;
            case "routingRemove": await Restart(() => Save(p => { p.RoutingProfiles.RemoveAll(r => r.Id == Text(data, "id")); if (p.ActiveRoutingProfile == Text(data, "id")) p.ActiveRoutingProfile = ""; })); return null;
            case "routingExport": return new { native = "save", name = "kot-routing.json", bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(RoutingProfiles.Serialize(profile.RoutingProfiles.Single(p => p.Id == Text(data, "id")).Rules))) };
            case "geoIpSettings": int hours = data.GetProperty("refreshHours").GetInt32(); new GeoIpOptions { RefreshHours = hours }.Validate(); Save(p => p.GeoIp.RefreshHours = hours); return null;
            case "geoIpRefresh": await geo.Update(Countries(), false, (path, ct) => GeoIp.ValidateFile(path, Paths.Core, ct), lifetime.Token, (code, ct) => GeoIp.Download(code, ct, NetworkRequests.Handler(tunnel.Port))); return null;
            case "testConnection":
                if (tools != null) throw new UserError("Проверка уже идёт.");
                if (tunnel.State != "connected") throw new UserError("Сначала подключите VPN.");
                tools = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); toolsTask = Quality(tools); return null;
            case "runDiagnostics":
                if (tools != null) throw new UserError("Проверка уже идёт.");
                tools = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); toolsTask = Diagnostics(data, tools); return null;
            case "checkUpdates": throw new UserError("Linux-канал подписанных обновлений ещё не опубликован. Windows Setup в Linux не устанавливается. Обновляйте .deb из доверенного источника.");
            default: throw new UserError("Команда пока не реализована в Linux: " + action);
        }
    }
    async Task Connect() { await tunnel.Connect(profile); if (tunnel.State == "connected") established = true; else { retry++; if (!profile.Automation.Reconnect || !established && retry >= 3) desired = false; nextRetry = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, 2 << Math.Min(retry, 5))); } }
    async Task Restart(Action update) { bool resume = desired; await tunnel.Stop(normal: !resume); update(); if (resume) { desired = true; await Connect(); } }
    async Task Import(string action, JsonElement data)
    {
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); owner.CancelAfter(TimeSpan.FromMinutes(3)); import = owner;
        try
        {
            string address = action == "refresh" ? profile.Address : Text(data, "url");
            string name = Text(data, "name", action == "refresh" ? profile.Name : "Локальные серверы").Trim();
            if (name.Length is < 1 or > 60) throw new UserError("Название: от 1 до 60 символов.");
            if (tunnel.Guard && tunnel.Port == 0) throw new UserError("Kill switch блокирует загрузку. Подключитесь или явно восстановите сеть.");
            var result = action == "importText" ? await ProviderRouting.ResolveLocal(Subscriptions.Parse(Text(data, "text")), owner.Token, tunnel.Port) : await Subscriptions.Download(address, owner.Token, device: profile.SendHwid ? Device() : null, proxyPort: tunnel.Port);
            await tunnel.Prepare(result.Nodes, owner.Token);
            result = await SubscriptionValidation.Filter(result, async (node, ct) => { try { await tunnel.Validate(node, ct); return null; } catch (UserError e) { return e.Message; } }, owner.Token);
            owner.Token.ThrowIfCancellationRequested();
            await Restart(() => Save(p =>
            {
                p.CaptureActive();
                var saved = action == "refresh" ? p.Subscriptions.Single(s => s.Id == p.ActiveSubscription) : p.Subscriptions.FirstOrDefault(s => !s.Local && address.Length > 0 && s.Address == address);
                if (saved == null) { if (p.Subscriptions.Count >= 50) throw new UserError("Максимум 50 подписок."); saved = new SavedSubscription(); p.Subscriptions.Add(saved); }
                saved.Name = name; saved.Address = action == "importText" ? "" : address; saved.Local = action == "importText"; saved.Nodes = result.Nodes; saved.Usage = result.Usage; saved.Updated = DateTimeOffset.UtcNow;
                if (saved.Selected != "auto" && saved.Nodes.All(n => n.Id != saved.Selected)) saved.Selected = saved.Nodes[0].Id;
                p.Activate(saved.Id);
            }));
            warnings = result.Warnings; Log("Импортировано серверов: " + result.Nodes.Count);
        }
        finally { import = null; }
    }
    SubscriptionDevice Device()
    {
        return new SubscriptionDevice(store.DeviceId(), "Linux", Environment.OSVersion.Version.ToString(), "Ubuntu Linux");
    }
    async Task Settings(JsonElement data)
    {
        string key = Text(data, "key"); var value = data.GetProperty("value");
        if (key == "startup")
        {
            string folder = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Paths.Home, ".config");
            string path = Path.Combine(folder, "autostart/io.github.prodkot.kot.desktop"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (value.GetBoolean()) Paths.Atomic(path, "[Desktop Entry]\nType=Application\nName=kot.\nExec=/usr/bin/kot --startup\nTerminal=false\n"u8.ToArray()); else if (File.Exists(path)) File.Delete(path);
        }
        if (key == "killSwitch" && !value.GetBoolean())
        {
            await tunnel.Stop();
            await OwnedProcess.Run("/usr/bin/pkexec", ["/usr/lib/kot/network-helper.py", "recover"]);
        }
        void Edit() => Save(p =>
        {
            switch (key)
            {
                case "theme": if (value.GetString() is not ("dark" or "light")) throw new UserError("Неизвестная тема."); p.Theme = value.GetString()!; break;
                case "mode": if (value.GetString() is not ("tun" or "proxy")) throw new UserError("Неизвестный режим."); p.ConnectionMode = value.GetString()!; break;
                case "bypass": p.Bypass = Configuration.Domains(value.GetString() ?? ""); p.Mode = "smart"; break;
                case "startup": p.Startup = value.GetBoolean(); break;
                case "tray": p.Tray = value.GetBoolean(); break;
                case "autoConnect": p.AutoConnect = value.GetBoolean(); break;
                case "sendHwid": p.SendHwid = value.GetBoolean(); break;
                case "killSwitch": p.KillSwitch = value.GetBoolean(); break;
                default: throw new UserError("Неизвестная настройка.");
            }
        });
        if (key is "mode" or "bypass" or "killSwitch") await Restart(Edit); else Edit();
    }
    string[] Countries() => profile.RoutingProfiles.SelectMany(p => p.Rules.Countries()).Distinct().ToArray();
    async Task PutRouting(string action, JsonElement data)
    {
        var old = profile.RoutingProfiles.FirstOrDefault(p => p.Id == Text(data, "id"));
        string source = action == "routingRefresh" ? old?.SourceUrl ?? "" : Text(data, "sourceUrl");
        RoutingRules rules;
        if (action == "routingImport")
        { string content = Text(data, "content").Trim(); source = content.StartsWith('{') ? "" : content; rules = source.Length == 0 ? RoutingProfiles.Parse(content) : await RoutingProfiles.Download(source, lifetime.Token, proxyPort: tunnel.Port); }
        else rules = source.Length > 0 ? await RoutingProfiles.Download(source, lifetime.Token, proxyPort: tunnel.Port) : RoutingProfiles.Parse(data.GetProperty("rules").GetRawText());
        if (rules.DirectApps.Concat(rules.ProxyApps).Concat(rules.BlockApps).Any()) throw new UserError("Windows .exe не имеют эквивалентной семантики в Linux. Правила приложений пока не перенесены.");
        var candidate = new RoutingProfile { Id = old?.Id ?? Guid.NewGuid().ToString("N"), Rules = rules, SourceUrl = source, Updated = DateTimeOffset.UtcNow, RefreshHours = data.TryGetProperty("refreshHours", out var hours) ? hours.GetInt32() : old?.RefreshHours ?? 24 }; candidate.Validate();
        await geo.Update(rules.Countries(), true, (path, ct) => GeoIp.ValidateFile(path, Paths.Core, ct), lifetime.Token, (code, ct) => GeoIp.Download(code, ct, NetworkRequests.Handler(tunnel.Port)));
        bool affectsConnection = action != "routingRefresh" || profile.ActiveRoutingProfile == candidate.Id;
        void Apply() => Save(p => { p.RoutingProfiles.RemoveAll(r => r.Id == candidate.Id); p.RoutingProfiles.Add(candidate); if (action != "routingRefresh") p.ActiveRoutingProfile = candidate.Id; });
        if (affectsConnection) await Restart(Apply); else Apply();
    }
    async Task Ping(Node[] nodes, CancellationTokenSource owner)
    {
        total = nodes.Length; done = 0; var options = profile.Ping;
        try
        {
            await Latency.Batch(nodes, options.Parallelism, async (node, ct) =>
            {
                pings[node.Id] = new("checking", Mode: options.Mode);
                try { pings[node.Id] = await Latency.Sample(options, token => options.Mode == "http" ? tunnel.Measure(node, options.Url, options.TimeoutMs, token) : Latency.Tcp(node.Outbound["server"]!.ToString(), node.Outbound["server_port"]!.GetValue<int>(), options.TimeoutMs, token), ct); }
                catch (OperationCanceledException) { pings[node.Id] = new("cancelled"); }
                catch { pings[node.Id] = new("error", Error: "Сервер недоступен или конфигурация не поддерживается."); }
                Interlocked.Increment(ref done);
            }, owner.Token);
        }
        catch (OperationCanceledException) { }
        finally { ping = null; owner.Dispose(); }
    }
    async Task Diagnostics(JsonElement data, CancellationTokenSource owner)
    {
        diagnostic.Clear(); toolKind = "diagnostics"; toolError = ""; toolStatus = "Проверка";
        try
        {
            var url = ConnectionTools.Website(Text(data, "url", profile.Ping.Url));
            int ms = tunnel.Port > 0 ? await Latency.Http(url.AbsoluteUri, tunnel.Port, profile.Ping.TimeoutMs, owner.Token) : await tunnel.Measure(Node(data), url.AbsoluteUri, profile.Ping.TimeoutMs, owner.Token);
            diagnostic.Enqueue(new { name = "HTTP через VPN", status = "ok", detail = ms + " мс" }); toolStatus = "Проверка завершена";
            if (tunnel.Port > 0)
            {
                using var client = ConnectionTools.Client(tunnel.Port);
                diagnostic.Enqueue(new { name = "DNS через VPN", status = "ok", detail = await ConnectionTools.DnsThroughVpn(client, url.IdnHost, owner.Token) });
                diagnostic.Enqueue(new { name = "Внешний IP", status = "ok", detail = await ConnectionTools.PublicIp(client, owner.Token) });
            }
        }
        catch (Exception e) { toolStatus = "Проверка не завершена"; toolError = e is OperationCanceledException ? "Отменено" : "DNS/TCP/TLS/HTTP проверка завершилась ошибкой."; }
        finally { tools = null; owner.Dispose(); }
    }
    async Task Quality(CancellationTokenSource owner)
    {
        toolKind = "connection"; qualitySamples.Clear(); quality = null; toolError = ""; toolStatus = "HTTP-проверка через VPN";
        int session = tunnel.Session, port = tunnel.HealthPort; var options = ConnectionCheck.Options(profile.Ping); checkAddress = options.Url;
        try
        {
            quality = await ConnectionCheck.Run(port, options, () => tunnel.Session == session && tunnel.State == "connected", ms =>
            { qualitySamples.Enqueue(ms); quality = ConnectionTools.Summarize(qualitySamples.ToArray()); }, owner.Token);
            toolStatus = quality.Successes == options.Attempts ? "Адрес доступен через VPN" : "Есть неуспешные запросы";
        }
        catch (OperationCanceledException) { toolStatus = "Проверка отменена или соединение изменилось"; }
        catch (Exception) { toolError = "HTTP-проверка не завершена. Проверьте соединение."; }
        finally { tools = null; owner.Dispose(); }
    }
    static string QrImage(Node node)
    {
        var matrix = QrCodes.Encode(NodeLinks.Export(node), 256); var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 256 256\"><path fill=\"white\" d=\"M0 0h256v256H0z\"/><path fill=\"black\" d=\"");
        for (int y = 0; y < matrix.Height; y++) for (int x = 0; x < matrix.Width; x++) if (matrix[x, y]) svg.Append($"M{x} {y}h1v1h-1z");
        svg.Append("\"/></svg>"); return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg.ToString()));
    }
    static object Qr(Node node) => new { node.Name, link = NodeLinks.Export(node), image = QrImage(node) };
    async Task Tick()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(lifetime.Token))
        {
            if (!ready || !await gate.WaitAsync(0)) continue;
            try
            {
                await tunnel.Watch();
                if (tunnel.State == "connected")
                {
                    if (DateTimeOffset.UtcNow >= nextHealth)
                    {
                        nextHealth = DateTimeOffset.UtcNow.AddSeconds(30);
                        if (!await tunnel.Probe(lifetime.Token))
                        {
                            await tunnel.Stop(normal: false); desired = profile.Automation.Reconnect && desired;
                            nextRetry = DateTimeOffset.UtcNow.AddSeconds(2); backgroundError = "Проверка VPN не прошла. " + (desired ? "Ожидание сети и повторное подключение." : "Восстановление отключено.");
                        }
                    }
                    try { var counters = await tunnel.Metrics(lifetime.Token); if (counters != null) { traffic = sampler.Sample(tunnel.Session, Tunnel.Clock / 1000d, counters); usage.Observe(tunnel.Session, Tunnel.Clock, DateTimeOffset.UtcNow, counters); UsageFile.Save(UsagePath, usage.Archive); } }
                    catch { backgroundError = "Статистика временно недоступна."; }
                }
                else { traffic = TrafficReading.Empty; if (desired && DateTimeOffset.UtcNow >= nextRetry && System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) await Connect(); }
                // Subscription refresh is bounded and never disables the guard.
                if (DateTimeOffset.UtcNow >= nextRefresh)
                {
                    nextRefresh = DateTimeOffset.UtcNow.AddMinutes(15);
                    if (profile.Automation.RefreshHours > 0 && !profile.Local && profile.Address.Length > 0 && DateTimeOffset.UtcNow - (profile.Updated ?? DateTimeOffset.MinValue) >= TimeSpan.FromHours(profile.Automation.RefreshHours))
                    { using var empty = JsonDocument.Parse("{}"); try { await Import("refresh", empty.RootElement); } catch { backgroundError = "Не удалось обновить подписку. Старая версия сохранена."; } }
                }
                if (DateTimeOffset.UtcNow >= nextRulesCheck && (!tunnel.Guard || tunnel.Port > 0) && System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                {
                    nextRulesCheck = DateTimeOffset.UtcNow.AddMinutes(1);
                    var due = profile.RoutingProfiles.FirstOrDefault(p => p.Due(DateTimeOffset.UtcNow) && (!routingAttempts.TryGetValue(p.Id, out var last) || DateTimeOffset.UtcNow - last >= TimeSpan.FromMinutes(15)));
                    if (due != null)
                    {
                        routingAttempts[due.Id] = DateTimeOffset.UtcNow;
                        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new { id = due.Id }));
                        try { await PutRouting("routingRefresh", request.RootElement); } catch (Exception e) when (e is not OperationCanceledException) { backgroundError = "Не удалось обновить маршрут. Рабочая версия сохранена."; }
                    }
                    if (geo.Due(Countries(), profile.GeoIp.RefreshHours, DateTimeOffset.UtcNow))
                    {
                        try
                        {
                            var changed = await geo.Update(Countries(), false, (path, ct) => GeoIp.ValidateFile(path, Paths.Core, ct), lifetime.Token, (code, ct) => GeoIp.Download(code, ct, NetworkRequests.Handler(tunnel.Port)));
                            if (changed.Length > 0 && desired) await Restart(() => { });
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { backgroundError = "Обновление GeoIP не завершено. Старый кэш сохранён."; }
                    }
                }
                Snapshot();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception e) { backgroundError = "Фоновая операция: " + e.GetType().Name; }
            finally { gate.Release(); }
        }
    }
    void Snapshot()
    {
        if (!ready) return;
        object Totals(UsageTotals t) => new { t.Download, t.Upload, connectedSeconds = t.ConnectedMilliseconds / 1000d, t.Sessions };
        var a = usage.Archive; var now = DateTimeOffset.UtcNow;
        Send(new { kind = "snapshot", data = new
        {
            name = profile.Name, hasSubscription = profile.Subscriptions.Count > 0, localSubscription = profile.Local,
            nodes = profile.Nodes.Select(n => new { n.Id, n.Name, n.Protocol, providerRouting = n.FullConfig != null, ping = pings.GetValueOrDefault(n.Id) }).ToArray(),
            subscriptions = profile.Subscriptions.Select(s => new { s.Id, s.Name, count = s.Nodes.Count }).ToArray(), activeSubscription = profile.ActiveSubscription,
            selected = profile.Selected, favorites = profile.Favorites, automaticNode = tunnel.AutomaticNode, state = tunnel.State, error = tunnel.Error, backgroundError,
            settings = new { profile.Theme, mode = profile.ConnectionMode, profile.Startup, profile.AutoConnect, profile.Tray, profile.SendHwid, profile.KillSwitch, killSwitchActive = tunnel.Guard, bypass = string.Join('\n', profile.Bypass) },
            routing = new { active = profile.ActiveRoutingProfile, profiles = profile.RoutingProfiles, errors = new { } },
            geoIp = new { refreshHours = profile.GeoIp.RefreshHours, countries = geo.Status(Countries()), busy = false },
            ping = new { busy = ping != null, done, total, settings = profile.Ping }, automation = profile.Automation,
            telemetry = new { traffic.Available, traffic.DownloadRate, traffic.UploadRate, traffic.DownloadTotal, traffic.UploadTotal, traffic.Count },
            statistics = new { running = tunnel.State == "connected", elapsedSeconds = tunnel.State == "connected" ? (Tunnel.Clock - tunnel.Started) / 1000d : (a.LastSession?.Totals.ConnectedMilliseconds ?? 0) / 1000d, session = Totals(a.LastSession?.Totals ?? new()), week = Totals(usage.Period(7, now)), month = Totals(usage.Period(30, now)), allTime = Totals(a.AllTime), a.FirstRecorded, started = a.LastSession?.Started, ended = a.LastSession?.Ended },
            tools = new { busy = tools != null, kind = toolKind, status = toolStatus, error = toolError, steps = diagnostic.ToArray(), quality, samples = qualitySamples.ToArray(), address = checkAddress },
            journal = journal.ToArray(), updates = new { status = "Linux: обновление через .deb", available = false }, warnings, version = ClientIdentity.Version, updated = profile.Updated?.ToLocalTime().ToString("dd.MM HH:mm")
        } });
    }
}
