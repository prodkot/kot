using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kot.Core;
namespace Kot.Windows;
public static class Store
{
    public static readonly string Folder = AppRuntime.DataFolder;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KotVPN.profile.v1");
    internal static ProfilePersistence Persistence(string? folder = null) => new(folder ?? Folder, Decode, Encode);
    static Profile Decode(byte[] encrypted)
    {
        byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try { var profile = JsonSerializer.Deserialize<Profile>(plain, Json) ?? throw new JsonException("Пустой профиль."); profile.Normalize(); return profile; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    static byte[] Encode(Profile profile)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(profile, Json);
        try { return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static Profile Load() => Persistence().Load();
    public static void Save(Profile profile) => Persistence().Save(profile);
    public static Profile Change(Profile profile, Action<Profile> change) => Persistence().Change(profile, change);
    public static string Friendly(Exception e) => e switch
    {
        UserError => e.Message,
        CryptographicException => "Не удалось расшифровать профиль для этой учётной записи Windows.",
        JsonException => "Файл данных повреждён или имеет неподдерживаемый формат.",
        OperationCanceledException => "Операция отменена или истекло время ожидания.",
        HttpRequestException => "Ошибка сети. Проверьте интернет и адрес сервера.",
        System.ComponentModel.Win32Exception native => $"Ошибка Windows {native.NativeErrorCode}: {native.Message}",
        IOException => "Не удалось записать данные. Проверьте доступ к папке приложения.",
        UnauthorizedAccessException => "Нет доступа к файлу. Проверьте права на папку и свободное место.",
        _ => e.GetType().Name + ": " + AppLog.Clean(e.Message) + "\nСкопируйте полный лог для разбора."
    };
}
