using Kot.Core;
using System.Net;
using System.Text.Json;
namespace Kot.Tests;
public static class ProductTests
{
    public static async Task Run(Action<bool, string> check, Node node, string fixtureFolder)
    {
        var fixture = JsonSerializer.Serialize(new
        {
            downloadTotal = 10000, uploadTotal = 2000,
            connections = new[] { new { id = "id-one", metadata = new { host = "example.org", destinationIP = "1.2.3.4", destinationPort = "443", network = "tcp", type = "tun/tun-in", processPath = @"C:\Users\Private\browser.exe (Private)" }, download = 1200, upload = 200, chains = new[] { "proxy" }, rule = "final", start = "2026-10-07T12:00:00Z" } }
        });
        var parsed = ConnectionSnapshot.Parse(fixture);
        check(parsed.Count == 1 && parsed.DownloadTotal == 10000 && parsed.UploadTotal == 2000, "Clash API cumulative traffic parsed");
        var c = parsed.Connections.Single();
        check(c.Destination == "example.org:443" && c.Network == "tcp" && c.Chains.SequenceEqual(new[] { "proxy" }), "connection destination, protocol and routing parsed");
        check(c.Process == "browser.exe" && !c.Process.Contains("Private") && c.Start != null, "connection view strips path and username");
        string ipv6 = fixture.Replace("example.org", "2001:db8::1"); check(ConnectionSnapshot.Parse(ipv6).Connections[0].Destination == "[2001:db8::1]:443", "IPv6 display has unambiguous port");
        var empty = ConnectionSnapshot.Parse("{\"downloadTotal\":0,\"uploadTotal\":0,\"connections\":null}"); check(empty.Count == 0, "empty core connection list accepted");
        foreach (string bad in new[] { fixture.Replace("\"downloadTotal\":10000", "\"downloadTotal\":-1"), "{\"downloadTotal\":0}", new string('x', 2 * 1024 * 1024 + 1) })
        { bool rejected = false; try { ConnectionSnapshot.Parse(bad); } catch (Exception ex) when (ex is JsonException or UserError) { rejected = true; } check(rejected, "invalid or oversized traffic response rejected"); }
        var sampler = new TrafficSampler();
        check(sampler.Sample(1, 10, parsed).DownloadRate == null, "first sample does not invent a speed");
        var speed = sampler.Sample(1, 12, parsed with { DownloadTotal = 14096, UploadTotal = 3024 });
        check(speed.DownloadRate == 2048 && speed.UploadRate == 512, "download/upload rate uses actual elapsed seconds");
        check(sampler.Sample(1, 15, parsed with { DownloadTotal = 14096, UploadTotal = 3024 }).DownloadRate == 0, "idle active session displays zero real throughput");
        check(sampler.Sample(2, 17, parsed).DownloadRate == null, "reconnect resets rate baseline");
        check(sampler.Sample(2, 16, parsed).DownloadRate == null, "non-increasing sample time does not divide by zero");
        check(sampler.Sample(2, 40, parsed).DownloadRate == null, "long gap does not display stale averaged speed");
        check(sampler.Sample(2, 42, empty).DownloadRate == null, "counter rollover resets baseline");
        sampler.Reset(); check(sampler.Sample(2, 44, parsed).DownloadRate == null, "API failure reset removes obsolete baseline");
        var conf = Configuration.Build(node, 19223, false, []); Configuration.Controller(conf, 19224, new string('A',64));
        check(conf["experimental"]?["clash_api"]?["external_controller"]?.ToString() == "127.0.0.1:19224" && conf["route"]?["find_process"]?.GetValue<bool>() == true, "manual connection has loopback API and process detection");
        check(conf["experimental"]?["clash_api"]?["access_control_allow_origin"]?[0]?.ToString() == "https://kot.local", "telemetry API CORS restricted to local UI");
        bool weak = false; try { Configuration.Controller(conf, 19224, "weak"); } catch(UserError) { weak = true; } check(weak, "controller refuses missing or weak secret");
        File.WriteAllText(Path.Combine(fixtureFolder, "telemetry-manual.json"), conf.ToJsonString());
        var github = JsonSerializer.Serialize(new { tag_name = "v0.3.1", draft = false, assets = new[] { new { name = "Kot-Client-0.3.1-beta-Windows-x64.zip", browser_download_url = "https://github.com/owner/repo/releases/download/v0.3.1/Kot-Client-0.3.1-beta-Windows-x64.zip", size = 1234 } } });
        var update = RemoteUpdates.Parse(github,"github",new Version("0.3.0")); check(update?.Version == "0.3.1" && update.Size == 1234, "GitHub release selects exact Windows asset");
        check(RemoteUpdates.Parse(github,"github",new Version("0.3.1")) == null && RemoteUpdates.Parse(github,"github",new Version("0.4.0")) == null, "remote update ignores same and older versions");
        string feed = "{\"version\":\"0.3.1\",\"url\":\"https://cdn.example/latest.zip\",\"size\":1234}";
        check(RemoteUpdates.Parse(feed,"https",new Version("0.3.0"))?.Url == "https://cdn.example/latest.zip", "VPS JSON feed parsed");
        foreach (var options in new[] { new UpdateOptions { Source = "github", Address = "https://github.com/a/b" }, new UpdateOptions { Source = "github", Address = "a/.." }, new UpdateOptions { Source = "https", Address = "http://example.com/feed" }, new UpdateOptions { Source = "https", Address = "https://user:password@example.com/feed" } })
        { bool rejected = false; try { options.Validate(); } catch(UserError) { rejected = true; } check(rejected,"invalid update source rejected"); }
        new UpdateOptions { Address = "owner/repo" }.Validate(); new UpdateOptions { Source = "https", Address = "https://example.com/feed" }.Validate();
        using var redirect = new HttpClient(new UpdateHandler((request, index) => index == 0 ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/next", UriKind.Relative) } } : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed) }));
        check((await RemoteUpdates.Check(new UpdateOptions { Source="https", Address="https://example.com/feed" }, new Version("0.3.0"), CancellationToken.None, redirect))?.Version == "0.3.1", "feed fetch follows safe relative HTTPS redirect");
        using var downgrade = new HttpClient(new UpdateHandler((_,_) => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://example.com/plain") } }));
        bool blocked = false; try { await RemoteUpdates.Check(new UpdateOptions { Source="https", Address="https://example.com/feed" }, new Version("0.3.0"), CancellationToken.None, downgrade); } catch(UserError) { blocked=true; } check(blocked,"update HTTPS downgrade redirect blocked");
        using var oversized = new HttpClient(new UpdateHandler((_,_) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x',1024*1024+1)) }));
        blocked=false; try { await RemoteUpdates.Check(new UpdateOptions { Source="https", Address="https://example.com/feed" }, new Version("0.3.0"), CancellationToken.None, oversized); } catch(UserError) { blocked=true; } check(blocked,"oversized metadata rejected before parsing");
        using var client = new HttpClient(new UpdateHandler((_,_) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] {1,2,3,4}) }));
        using var output = new MemoryStream(); long progress = 0; await RemoteUpdates.Download(update! with { Size = 4 },output,CancellationToken.None,n=>progress=n,client); check(output.ToArray().SequenceEqual(new byte[]{1,2,3,4}) && progress==4,"update download writes actual bytes and reports progress");
        using var stalled = new HttpClient(new UpdateHandler((_,_) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) }));
        using var deadline = new CancellationTokenSource(100); blocked=false; try { await RemoteUpdates.Download(update!,new MemoryStream(),deadline.Token,client:stalled); } catch(OperationCanceledException) { blocked=true; } check(blocked,"cancel interrupts stalled update response body");
        var saved = new Profile { Updates = new UpdateOptions { Address="owner/repo", Automatic=false } }; var loaded=JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(saved))!; loaded.Normalize(); check(loaded.Updates.Address=="owner/repo"&&!loaded.Updates.Automatic,"update preferences survive profile serialization");
        var journal = new SessionLog(1000); journal.Write("test",new string('a',900)); journal.Write("test","last-entry"); check(journal.Tail(80).Contains("last-entry") && journal.Tail(80).Length < 180,"log view bounded tail preserves newest entries");
    }
    sealed class UpdateHandler(Func<HttpRequestMessage,int,HttpResponseMessage> reply) : HttpMessageHandler
    {
        int index;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => Task.FromResult(reply(request,index++));
    }
    sealed class StalledBody : Stream
    {
        public override bool CanRead=>true; public override bool CanWrite=>false; public override bool CanSeek=>false;
        public override long Length=>throw new NotSupportedException(); public override long Position { get=>0;set=>throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default) { await Task.Delay(Timeout.InfiniteTimeSpan,ct);return 0; }
        public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException(); public override void Flush(){} public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    }
}
