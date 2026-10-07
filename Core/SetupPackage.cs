using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Kot.Core;

// One public Setup contains an NSIS installer and a signed update archive.
// Authenticate both sections before exposing the embedded ZIP to the updater.
public static class SetupPackage
{
    public const string Magic = "KOT.SETUP.V1.END";
    public sealed record Manifest(string Version, long InstallerLength, long ArchiveLength, string InstallerHash, string ArchiveHash);
    public static ZipArchive Open(Stream file, Version? current = null, string? expected = null, string? publicKey = null)
    {
        if (!file.CanSeek || file.Length < 24 || file.Length > RemoteUpdates.MaximumArchive) throw new UserError("Некорректный установщик обновления.");
        file.Position = file.Length - 24; byte[] footer = new byte[24]; file.ReadExactly(footer);
        int manifestLength = BinaryPrimitives.ReadInt32LittleEndian(footer), signatureLength = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(4));
        if (Encoding.ASCII.GetString(footer, 8, 16) != Magic || manifestLength is < 1 or > 4096 || signatureLength is < 128 or > 1024 || (long)manifestLength + signatureLength + 24 >= file.Length) throw new UserError("Установщик не содержит подписанного обновления kot.");
        long metadata = file.Length - 24 - manifestLength - signatureLength;
        file.Position = metadata; byte[] json = new byte[manifestLength], signature = new byte[signatureLength]; file.ReadExactly(json); file.ReadExactly(signature);
        using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey ?? ReleaseKey.Public);
        if (!rsa.VerifyData(json, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new UserError("Подпись установщика не прошла проверку.");
        var manifest = JsonSerializer.Deserialize<Manifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new UserError("Пустой манифест установщика.");
        if (!Version.TryParse(manifest.Version, out var version) || current != null && version <= current || expected != null && expected != manifest.Version || manifest.InstallerLength < 2 || manifest.ArchiveLength < 22 || manifest.InstallerLength > metadata || manifest.ArchiveLength != metadata - manifest.InstallerLength) throw new UserError("Версия или размер установщика не совпадает с обновлением.");
        file.Position = 0; if (file.ReadByte() != 'M' || file.ReadByte() != 'Z') throw new UserError("Это не установщик Windows.");
        foreach (var part in new[] { (Offset: 0L, Length: manifest.InstallerLength, Hash: manifest.InstallerHash), (Offset: manifest.InstallerLength, Length: manifest.ArchiveLength, Hash: manifest.ArchiveHash) })
        {
            using var slice = new Slice(file, part.Offset, part.Length);
            if (!Convert.ToHexString(SHA256.HashData(slice)).Equals(part.Hash, StringComparison.OrdinalIgnoreCase)) throw new UserError("Установщик повреждён или изменён после подписи.");
        }
        return new ZipArchive(new Slice(file, manifest.InstallerLength, manifest.ArchiveLength), ZipArchiveMode.Read);
    }
    sealed class Slice(Stream source, long offset, long length) : Stream
    {
        long position;
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set { if (value < 0 || value > length) throw new IOException("Read outside setup archive."); position = value; } }
        public override int Read(byte[] buffer, int offsetInBuffer, int count) => Read(buffer.AsSpan(offsetInBuffer, count));
        public override int Read(Span<byte> buffer) { source.Position = offset + position; int count = source.Read(buffer[..(int)Math.Min(buffer.Length, length - position)]); position += count; return count; }
        public override long Seek(long value, SeekOrigin origin) { Position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? position : length) + value; return position; }
        public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
