using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kot.Core;
namespace Kot.Windows;
public static class Store
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KotVPN");
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KotVPN.profile.v1");
    public static Profile Load()
    {
        Directory.CreateDirectory(Folder); string path = Path.Combine(Folder, "profile.bin");
        if (!File.Exists(path)) return new();
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            try { var profile = JsonSerializer.Deserialize<Profile>(plain, Json) ?? new(); profile.Normalize(); return profile; }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch { throw new UserError("Не удалось открыть сохранённую подписку. Файл profile.bin оставлен в папке KotVPN; переименуйте его, чтобы начать заново."); }
    }
    public static void Save(Profile profile)
    {
        profile.CaptureActive();
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(profile, Json);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); } finally { CryptographicOperations.ZeroMemory(plain); }
        string path = Path.Combine(Folder, "profile.bin"), temp = path + ".tmp";
        File.WriteAllBytes(temp, encrypted); File.Move(temp, path, true);
    }
    public static string Friendly(Exception e) => e switch
    {
        UserError => e.Message,
        OperationCanceledException => "Операция отменена или истекло время ожидания.",
        HttpRequestException => "Ошибка сети. Проверьте интернет и адрес сервера.",
        System.ComponentModel.Win32Exception native => $"Ошибка Windows {native.NativeErrorCode}: {native.Message}",
        IOException => "Не удалось записать данные. Проверьте доступ к папке приложения.",
        _ => e.GetType().Name + ": " + AppLog.Clean(e.Message) + "\nСкопируйте полный лог для разбора."
    };
}
