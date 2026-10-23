using System.Security.Cryptography;
using System.Text.Json;
using Kot.Core;
namespace Kot.Linux;

public sealed class Store(byte[] key) : IDisposable
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    ProfilePersistence Persistence => new(Paths.Data, Decode, Encode);
    public static async Task<Store> Open()
    {
        bool exists = Directory.GetFiles(Paths.Data, "profile*.bin").Length > 0;
        string result;
        try { result = await OwnedProcess.Run("/usr/bin/python3", [Path.Combine(AppContext.BaseDirectory, "keyring.py"), exists ? "lookup" : "create"]); }
        catch { throw new UserError("Secret Service недоступен или заблокирован. Разблокируйте связку ключей GNOME. Профиль не изменён; открытое хранение запрещено."); }
        return new(Convert.FromBase64String(result));
    }
    byte[] Encode(Profile profile)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(profile, Json), nonce = RandomNumberGenerator.GetBytes(12);
        byte[] encrypted = new byte[32 + plain.Length]; "KOTL"u8.CopyTo(encrypted); nonce.CopyTo(encrypted, 4);
        try { using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, encrypted.AsSpan(32), encrypted.AsSpan(16, 16), encrypted.AsSpan(0, 4)); return encrypted; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    Profile Decode(byte[] encrypted)
    {
        if (encrypted.Length < 32 || !encrypted.AsSpan(0, 4).SequenceEqual("KOTL"u8)) throw new UserError("Это не Linux-профиль. Перенесите данные Windows через .kotbackup с паролем.");
        byte[] plain = new byte[encrypted.Length - 32];
        try { using var aes = new AesGcm(key, 16); aes.Decrypt(encrypted.AsSpan(4, 12), encrypted.AsSpan(32), encrypted.AsSpan(16, 16), plain, encrypted.AsSpan(0, 4)); var value = JsonSerializer.Deserialize<Profile>(plain, Json) ?? throw new JsonException(); value.Validate(); return value; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public Profile Load() => Persistence.Load();
    public string DeviceId() => Convert.ToHexString(HMACSHA256.HashData(key, "kot.linux.device.v1"u8));
    public Profile Change(Profile profile, Action<Profile> edit) => Persistence.Change(profile, edit);
    public void Save(Profile profile) => Persistence.Save(profile);
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}
