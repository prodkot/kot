using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kot.Core;
namespace Kot.Tests;
public static class SetupTests
{
    public static void Run(Action<bool,string> check)
    {
        using var rsa = RSA.Create(2048); byte[] prefix = Encoding.ASCII.GetBytes("MZinstaller-fixture");
        using var zipMemory = new MemoryStream();
        using (var zip = new ZipArchive(zipMemory,ZipArchiveMode.Create,true)) { using var text = new StreamWriter(zip.CreateEntry("check.txt").Open()); text.Write("payload"); }
        byte[] zipBytes = zipMemory.ToArray();
        byte[] Build(bool badLength = false)
        {
            var manifest = new SetupPackage.Manifest("0.5.0", prefix.Length, zipBytes.Length + (badLength ? 1 : 0), Convert.ToHexString(SHA256.HashData(prefix)), Convert.ToHexString(SHA256.HashData(zipBytes)));
            byte[] json=JsonSerializer.SerializeToUtf8Bytes(manifest), signature=rsa.SignData(json,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1), footer=new byte[24];
            BinaryPrimitives.WriteInt32LittleEndian(footer,json.Length); BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(4),signature.Length); Encoding.ASCII.GetBytes(SetupPackage.Magic).CopyTo(footer,8);
            return prefix.Concat(zipBytes).Concat(json).Concat(signature).Concat(footer).ToArray();
        }
        using (var source = new MemoryStream(Build())) using (var archive = SetupPackage.Open(source,new Version("0.4.2"),"0.5.0",rsa.ExportSubjectPublicKeyInfoPem()))
        using (var text = new StreamReader(archive.GetEntry("check.txt")!.Open())) check(text.ReadToEnd()=="payload", "single Setup exposes authenticated embedded update payload");
        foreach(string kind in new[]{"installer","archive","signature","footer","length","version","downgrade","key","truncated"})
        {
            byte[] bytes=Build(kind=="length");
            if(kind=="installer")bytes[3]^=1; if(kind=="archive")bytes[prefix.Length+5]^=1; if(kind=="signature")bytes[^30]^=1; if(kind=="footer")bytes[^1]^=1; if(kind=="truncated")bytes=bytes[..^1];
            bool rejected=false;
            try { using var source=new MemoryStream(bytes); using var archive=SetupPackage.Open(source,new Version(kind=="downgrade"?"0.5.0":"0.4.2"),kind=="version"?"0.5.1":"0.5.0",kind=="key"?ReleaseKey.Public:rsa.ExportSubjectPublicKeyInfoPem()); }
            catch(UserError){rejected=true;}
            check(rejected,"Setup rejects "+kind+" before any update writes");
        }
        string release=JsonSerializer.Serialize(new{tag_name="v0.5.0",assets=new[]{new{name="Kot-Setup-0.5.0-Windows-x64.exe",browser_download_url="https://example.com/setup.exe",size=1234}}});
        check(RemoteUpdates.Parse(release,"github",new Version("0.4.2"))?.Url=="https://example.com/setup.exe","updater selects the sole Setup asset without a portable ZIP");
    }
}
