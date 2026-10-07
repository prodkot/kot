using Kot.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Kot.Tests;
public static class UpdateTests
{
    public static async Task Run(Action<bool,string> check, string folder)
    {
        using var rsa = RSA.Create(2048);
        var files = new Dictionary<string,byte[]> { ["Kot.exe"] = Encoding.UTF8.GetBytes("new app"), ["core/sing-box.exe"] = Encoding.UTF8.GetBytes("new core"), ["ui/index.html"] = Encoding.UTF8.GetBytes("new UI"), ["new-file.txt"] = Encoding.UTF8.GetBytes("new") };
        byte[] Package(bool tampered = false, bool collision = false)
        {
            var payload = new Dictionary<string,byte[]>(files); if (collision) { payload["ui"] = [1]; }
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest("0.4.1", payload.ToDictionary(p=>p.Key,p=>Convert.ToHexString(SHA256.HashData(p.Value)))));
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream,ZipArchiveMode.Create,true))
            {
                void Add(string name, byte[] data) { using var e = zip.CreateEntry(name).Open(); e.Write(data); }
                Add("release.json",manifest); Add("release.sig",rsa.SignData(manifest,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));
                foreach (var p in payload) Add(p.Key, tampered && p.Key == "Kot.exe" ? [0] : p.Value);
            }
            return stream.ToArray();
        }
        string root = Path.Combine(folder,"update-tests"); Directory.CreateDirectory(root);
        string Old(string name) => Path.Combine(root,name.Replace('/',Path.DirectorySeparatorChar));
        void Reset() { if (Directory.Exists(root)) Directory.Delete(root,true); Directory.CreateDirectory(root); Directory.CreateDirectory(Old("core")); Directory.CreateDirectory(Old("ui")); File.WriteAllText(Old("Kot.exe"),"old app"); File.WriteAllText(Old("core/sing-box.exe"),"old core"); File.WriteAllText(Old("ui/index.html"),"old UI"); File.WriteAllText(Old("user-note.txt"),"keep"); }
        for (int fail = 1; fail <= files.Count + 2; fail++)
        {
            Reset(); string work = Path.Combine(root,".kot-update-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
            using var zip = new ZipArchive(new MemoryStream(Package()),ZipArchiveMode.Read);
            bool failed = false;
            try { new UpdateTransaction(root,work).Apply(zip,new Version("0.4.0"),publicKey:rsa.ExportSubjectPublicKeyInfoPem(),afterWrite: count=>{if(count==fail)throw new IOException("injected rename failure");}); }
            catch(IOException) { failed=true; }
            check(failed && File.ReadAllText(Old("Kot.exe"))=="old app" && File.ReadAllText(Old("core/sing-box.exe"))=="old core" && File.ReadAllText(Old("ui/index.html"))=="old UI" && !File.Exists(Old("new-file.txt")) && File.ReadAllText(Old("user-note.txt"))=="keep", "rollback after write " + fail + " restores app/core/UI and preserves user files");
        }
        Reset(); string success = Path.Combine(root,".kot-update-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(success);
        using (var zip = new ZipArchive(new MemoryStream(Package()),ZipArchiveMode.Read))
        { var t = new UpdateTransaction(root,success); t.Apply(zip,new Version("0.4.0"),"0.4.1",rsa.ExportSubjectPublicKeyInfoPem()); check(File.ReadAllText(Old("Kot.exe"))=="new app" && File.ReadAllText(Old("user-note.txt"))=="keep","update applies in place and preserves unrelated files"); new UpdateTransaction(root,success).Rollback(); check(File.ReadAllText(Old("Kot.exe"))=="old app" && !File.Exists(Old("new-file.txt")),"journal allows rollback by a fresh updater instance"); }
        foreach(var kind in new[]{"tampered","collision","version","key"})
        {
            Reset(); string work=Path.Combine(root,".kot-update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
            using var zip = new ZipArchive(new MemoryStream(Package(kind=="tampered",kind=="collision")),ZipArchiveMode.Read);
            bool blocked=false;try {new UpdateTransaction(root,work).Apply(zip,new Version("0.4.0"),kind=="version"?"0.4.2":"0.4.1",kind=="key"?ReleaseKey.Public:rsa.ExportSubjectPublicKeyInfoPem());}catch(UserError){blocked=true;}
            check(blocked && File.ReadAllText(Old("Kot.exe"))=="old app" && !File.Exists(Path.Combine(work,"journal.json")),"update rejects "+kind+" before changing installed files");
        }
        foreach (bool parentFile in new[]{false,true})
        {
            Reset(); if(parentFile) { Directory.Delete(Old("ui"),true); File.WriteAllText(Old("ui"),"keep-file"); } else { File.Delete(Old("ui/index.html")); Directory.CreateDirectory(Old("ui/index.html")); }
            string work=Path.Combine(root,".kot-update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
            bool blocked=false;using(var zip=new ZipArchive(new MemoryStream(Package()),ZipArchiveMode.Read))try{new UpdateTransaction(root,work).Apply(zip,new Version("0.4.0"),publicKey:rsa.ExportSubjectPublicKeyInfoPem());}catch(UserError){blocked=true;}
            check(blocked && File.ReadAllText(Old("Kot.exe"))=="old app" && !File.Exists(Path.Combine(work,"journal.json")),"destination file/directory collision rejected before writes: "+parentFile);
        }
        Reset(); string external=Path.Combine(folder,"outside-update");Directory.CreateDirectory(external); File.WriteAllText(Path.Combine(external,"sing-box.exe"),"external");Directory.Delete(Old("core"),true); Directory.CreateSymbolicLink(Old("core"),Path.GetFullPath(external));
        bool linked=false;string linkedWork=Path.Combine(root,".kot-update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(linkedWork);
        using(var zip=new ZipArchive(new MemoryStream(Package()),ZipArchiveMode.Read))try{new UpdateTransaction(root,linkedWork).Apply(zip,new Version("0.4.0"),publicKey:rsa.ExportSubjectPublicKeyInfoPem());}catch(UserError){linked=true;}
        check(linked && File.ReadAllText(Path.Combine(external,"sing-box.exe"))=="external","update rejects existing symlink/junction destinations without outside writes");Directory.Delete(Old("core"));Directory.Delete(root,true);Directory.Delete(external,true);
        using var client=new HttpClient(new Fixture());using var output=new MemoryStream();bool truncated=false;
        try{await RemoteUpdates.Download(new("0.4.1","https://example.com/update.zip",10),output,CancellationToken.None,client:client);}catch(UserError){truncated=true;}
        check(truncated,"download rejects bytes differing from advertised release size");
        string json="{\"tag_name\":\"v0.4.1\",\"prerelease\":true,\"assets\":[]}";
        check(RemoteUpdates.Parse(json,new Version("0.4.0"))==null,"automatic channel ignores GitHub prereleases");
    }
    sealed class Fixture:HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new ByteArrayContent([1,2,3])}); }
}
