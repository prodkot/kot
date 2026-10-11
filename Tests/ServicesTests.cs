using Kot.Core;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Kot.Tests;
public static class ServicesTests
{
    sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request)); }
    public static async Task Run(Action<bool,string> check, string generated)
    {
        string folder = Path.Combine(generated, "services"); Directory.CreateDirectory(folder);
        void Reject(Action action, string name) { bool bad = false; try { action(); } catch (Exception ex) when (ex is UserError or JsonException or InvalidOperationException or UriFormatException or IOException) { bad = true; } check(bad, name); }
        var options = new ServiceOptions(); options.Validate();
        var clone = JsonSerializer.Deserialize<ServiceOptions>(JsonSerializer.Serialize(options))!;
        check(clone.Telegram.Secret == options.Telegram.Secret && clone.Telegram.Secret.Length == 32, "service settings and generated MTProto secret survive serialization");
        using (var cfg = JsonDocument.Parse(options.Telegram.PortableConfig()))
        {
            var c = cfg.RootElement;
            check(c.GetProperty("host").GetString() == "127.0.0.1" && !c.GetProperty("check_updates").GetBoolean() && !c.GetProperty("autostart").GetBoolean(), "official Telegram portable configuration binds only loopback and kot owns updates/autostart");
            check(c.GetProperty("secret").GetString() == options.Telegram.Secret && options.Telegram.Link.EndsWith("secret=dd" + options.Telegram.Secret), "MTProto link matches official dd-secret format");
        }
        foreach (int port in new[] {0,443,65536}) Reject(() => new TelegramProxyOptions { Port=port }.Validate(), "invalid Telegram port " + port);
        foreach (string dc in new[] {"not-a-dc", "0:149.154.167.220", "2:127.0.0.1", "2:0.0.0.0"}) Reject(() => new TelegramProxyOptions { DcIp=dc }.Validate(), "invalid Telegram DC rejected " + dc);
        Reject(() => new TelegramProxyOptions { Secret="123" }.Validate(), "short MTProto secret rejected");
        Reject(() => new TelegramProxyOptions { Domains="https://evil.example/path" }.Validate(), "worker/domain settings reject URLs");
        Reject(() => new ZapretOptions { Strategy="../general.bat" }.Validate(), "saved strategy cannot escape package");
        foreach (string url in new[] {"http://discord.com", "https://user:password@discord.com", "https://127.0.0.1", "https://localhost", "https://discord.com:8443", "https://discord.com#fragment"}) Reject(() => ServiceProbe.Targets(url), "test target rejected " + url);
        check(ServiceProbe.Targets("https://discord.com\nhttps://discord.com\r\nhttps://www.youtube.com").Length == 2, "test targets normalize duplicate lines");
        check(WindowsArguments.Quote("c:\\test path\\") == "\"c:\\test path\\\\\"" && WindowsArguments.Quote("a\"b") == "\"a\\\"b\"", "native launcher preserves spaces, trailing backslashes and literal quotes");
        Reject(() => WindowsArguments.Quote("bad\narg"), "native launcher rejects control characters");
        byte[] payload = Encoding.UTF8.GetBytes("MZ fixture release package"); string hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        string Release(string id, string? digest = null, string? url = null, bool prerelease = false)
        {
            string version=id=="telegram"?"v1.11.2":"1.10.3";
            string name=id=="telegram"?"TgWsProxy_windows.exe":"zapret-discord-youtube-1.10.3.zip";
            return JsonSerializer.Serialize(new {tag_name=version,draft=false,prerelease,assets=new[]{new{name,size=payload.Length,digest=digest??"sha256:"+hash,browser_download_url=url??$"https://github.com/{ServicePackages.Repository(id)}/releases/download/{version}/{name}"}}});
        }
        var release = ServicePackages.Parse("telegram", Release("telegram"));
        check(release.Version == "v1.11.2" && ServicePackages.Newer("v1.11.2", "v1.10.9") && !ServicePackages.Newer("1.9.2", "1.10.3"), "stable service releases and numeric version comparison");
        Reject(() => ServicePackages.Parse("telegram", Release("telegram", url:"https://evil.example/TgWsProxy_windows.exe")), "service release rejects substituted download host");
        Reject(() => ServicePackages.Parse("telegram", Release("telegram", digest:"sha256:bad")), "service release requires full SHA-256");
        Reject(() => ServicePackages.Parse("telegram", Release("telegram", prerelease:true)), "service release rejects prerelease");
        Reject(() => ServicePackages.Repository("other"), "only the two configured upstreams can be downloaded");
        using(var handler=new Transport(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(payload)}))
        {
            string path=Path.Combine(folder,"download-ok"); File.Delete(path);
            await ServicePackages.Download(release,path,null,CancellationToken.None,handler);
            check(File.ReadAllBytes(path).SequenceEqual(payload), "download verifies exact bytes, length and SHA-256");
        }
        async Task DownloadReject(ServiceRelease value, Transport transport, string name)
        {
            using(transport){string path=Path.Combine(folder,"download-bad");File.Delete(path);bool bad=false;
            try{await ServicePackages.Download(value,path,null,CancellationToken.None,transport);}catch(UserError){bad=true;}
            check(bad&&!File.Exists(path), name);}
        }
        await DownloadReject(release with { Sha256=new string('0',64) },new(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(payload)}),"bad digest removes partial service download");
        await DownloadReject(release,new(_=>new(HttpStatusCode.Redirect){Headers={Location=new Uri("http://github.com/plain")}}),"download rejects HTTPS downgrade");
        await DownloadReject(release,new(_=>new(HttpStatusCode.Redirect){Headers={Location=new Uri("https://evil.example/package")}}),"download rejects external redirect");
        await DownloadReject(release,new(_=>new(HttpStatusCode.Redirect){Headers={Location=new Uri("https://github.com/loop")}}),"download redirect loop is bounded");
        using(var cts=new CancellationTokenSource())
        using(var handler=new Transport(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(payload)}))
        {cts.Cancel();string path=Path.Combine(folder,"cancelled");bool cancelled=false;try{await ServicePackages.Download(release,path,null,cts.Token,handler);}catch(OperationCanceledException){cancelled=true;}check(cancelled&&!File.Exists(path),"cancelled download leaves no partial package");}
        void Archive(string path, params string[] files)
        {File.Delete(path);using var zip=ZipFile.Open(path,ZipArchiveMode.Create);foreach(string file in files){using var entry=zip.CreateEntry(file).Open();entry.Write(payload);}}
        string archive=Path.Combine(folder,"fixture.zip");
        foreach(string bad in new[]{"../outside", "/absolute", "bin/../../outside", "bin/x:stream", "bin/NUL.txt", "bin/filename. ", "bin/CON", "bin\\..\\escape"})
        {
            Archive(archive,bad);string target=Path.Combine(folder,"extract-"+Guid.NewGuid().ToString("N"));Reject(()=>ServicePackages.Extract(archive,target,CancellationToken.None),"service archive rejects "+bad);
        }
        Archive(archive,"bin/test","bin/TEST");Reject(()=>ServicePackages.Extract(archive,Path.Combine(folder,"collision"),CancellationToken.None),"service archive rejects case-insensitive filename collision");
        Archive(archive,"release/bin/winws.exe","release/lists/test.txt");string valid=Path.Combine(folder,"valid-"+Guid.NewGuid().ToString("N"));ServicePackages.Extract(archive,valid,CancellationToken.None);check(File.Exists(Path.Combine(valid,"release/bin/winws.exe")),"valid service archive is extracted inside its destination");
        string bin=Path.Combine(folder,"bin with spaces"),lists=Path.Combine(folder,"lists with spaces");Directory.CreateDirectory(bin);Directory.CreateDirectory(lists);File.WriteAllText(Path.Combine(bin,"fake.bin"),"fixture");File.WriteAllText(Path.Combine(lists,"list.txt"),"example.com");
        string strategy="@echo off\r\ncall service.bat\r\nstart \"zapret\" /min \"%BIN%winws.exe\" --wf-tcp=80,443,%GameFilterTCP% ^\r\n--hostlist=\"%LISTS%list.txt\" --dpi-desync-fake-tls=\"%BIN%fake.bin\" --new\r\necho unrelated shell tail";
        var parsed=ZapretStrategy.Parse(strategy,bin,lists,true);
        check(parsed.Length==4&&parsed[0].EndsWith("1024-65535")&&parsed[1]=="--hostlist="+Path.GetFullPath(Path.Combine(lists,"list.txt")),"zapret parser expands official variables and preserves quoted file paths without executing BAT");
        check(ZapretStrategy.Parse(strategy,bin,lists,false)[0].EndsWith(",12"),"disabled game filter matches official sentinel");
        foreach(string bad in new[]{" & calc.exe", " | command", " --unknown=%UNKNOWN%", " --hostlist=\"../outside\"", " --broken=\"open"}) Reject(()=>ZapretStrategy.Parse(strategy.Split("\r\necho")[0]+bad,bin,lists,false),"unsafe strategy command rejected "+bad);
        var baseline=new List<ServiceProbeResult>{new("https://discord.com/",false,6000,null,"timeout"),new("https://www.youtube.com/",true,100,200,"")};
        var run=new StrategyTestRun{Baseline=baseline,Results=[new("general.bat",[new("https://discord.com/",true,150,200,""),new("https://www.youtube.com/",true,100,200,"")]),new("general (ALT).bat",[new("https://discord.com/",true,50,200,""),new("https://www.youtube.com/",true,70,200,"")])]};
        check(run.Recommended=="general (ALT).bat","strategy recommendation compares baseline, success count, then latency");
        run.Baseline=run.Results[0].Checks;check(run.Recommended==null,"already reachable baseline cannot falsely recommend zapret");
        run.Baseline=baseline;run.Results=[new("general.bat",run.Results[0].Checks,"worker exited")];check(run.Recommended==null,"failed worker cannot be recommended despite reachable targets");
        var saved=JsonSerializer.Deserialize<StrategyTestRun>(JsonSerializer.Serialize(run))!;check(saved.Version==run.Version&&saved.Results.Count==1,"test history preserves checks and failure state");
    }
}
