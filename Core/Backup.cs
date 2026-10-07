using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Kot.Core;
public static class Backup
{
    const int Maximum = 20 * 1024 * 1024;
    static readonly byte[] Magic = "KOTBACK1"u8.ToArray();
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static byte[] Export(Profile profile, string password)
    {
        Password(password); profile.CaptureActive(); profile.Validate();
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(profile, Json);
        if (plain.Length > Maximum) { CryptographicOperations.ZeroMemory(plain); throw new UserError("Резервная копия слишком большая."); }
        byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 300_000, HashAlgorithmName.SHA256, 32);
        byte[] result = new byte[52 + plain.Length]; Magic.CopyTo(result, 0); salt.CopyTo(result, 8); nonce.CopyTo(result, 24);
        try { using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, result.AsSpan(52), result.AsSpan(36, 16), result.AsSpan(0, 36)); return result; }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key); }
    }
    public static Profile Import(byte[] data, string password)
    {
        Password(password);
        if (data.Length is < 52 or > Maximum + 52 || !data.AsSpan(0, 8).SequenceEqual(Magic)) throw new UserError("Это не резервная копия kot.");
        byte[] plain = new byte[data.Length - 52];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, data.AsSpan(8, 16), 300_000, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16); aes.Decrypt(data.AsSpan(24, 12), data.AsSpan(52), data.AsSpan(36, 16), plain, data.AsSpan(0, 36));
            var profile = JsonSerializer.Deserialize<Profile>(plain, Json) ?? throw new UserError("Пустая резервная копия.");
            profile.Validate(); return profile;
        }
        catch (CryptographicException) { throw new UserError("Неверный пароль или повреждённая резервная копия."); }
        catch (JsonException) { throw new UserError("Повреждённая резервная копия."); }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key); }
    }
    static void Password(string password) { if (password.Length is < 8 or > 1024) throw new UserError("Пароль: от 8 до 1024 символов."); }
}
