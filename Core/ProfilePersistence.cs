namespace Kot.Core;

// Encryption remains the platform's responsibility. All replacement paths keep
// the previous bytes until a complete, validated new profile is on disk.
public sealed class ProfilePersistence(string folder, Func<byte[], Profile> decode, Func<Profile, byte[]> encode)
{
    public string PrimaryPath => Path.Combine(folder, "profile.bin");
    public string BackupPath => Path.Combine(folder, "profile.previous.bin");
    const int MaximumSize = 20 * 1024 * 1024;
    Profile Read(string path)
    {
        if (new FileInfo(path).Length > MaximumSize) throw new UserError("Файл профиля слишком большой.");
        return decode(File.ReadAllBytes(path));
    }
    public Profile Load()
    {
        Directory.CreateDirectory(folder);
        if (File.Exists(PrimaryPath)) return Read(PrimaryPath);
        if (File.Exists(BackupPath)) throw new UserError("Основной профиль отсутствует. Можно восстановить предыдущую копию.");
        return new();
    }
    public Profile LoadBackup() => Read(BackupPath);
    public Profile Change(Profile current, Action<Profile> change)
    {
        byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(current);
        Profile candidate;
        try { candidate = System.Text.Json.JsonSerializer.Deserialize<Profile>(bytes)!; }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        candidate.CaptureActive(); change(candidate); Save(candidate); return candidate;
    }
    public void Save(Profile profile)
    {
        // Do not replace the last good backup with bytes corrupted externally.
        if (File.Exists(PrimaryPath)) Read(PrimaryPath);
        Write(profile, keepPrevious: true);
    }
    void Write(Profile profile, bool keepPrevious)
    {
        Directory.CreateDirectory(folder); profile.CaptureActive();
        byte[] bytes = encode(profile);
        if (bytes.Length > MaximumSize) throw new UserError("Профиль слишком большой для сохранения.");
        decode(bytes);
        string temporary = Path.Combine(folder, "profile-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            if (keepPrevious && File.Exists(PrimaryPath)) File.Replace(temporary, PrimaryPath, BackupPath, true);
            else File.Move(temporary, PrimaryPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    void Preserve(string path)
    {
        if (File.Exists(path)) File.Copy(path, Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "-recovery-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bin"));
    }
    public Profile RestoreBackup()
    {
        var profile = LoadBackup(); Preserve(PrimaryPath); Write(profile, keepPrevious: false); return profile;
    }
    public void Replace(Profile profile)
    {
        // Called only after an explicit import/reset choice. Preserve both old
        // files, even when neither can be decoded, for later recovery.
        Preserve(PrimaryPath); Preserve(BackupPath); Write(profile, keepPrevious: false);
        if (File.Exists(BackupPath)) File.Delete(BackupPath);
    }
}
