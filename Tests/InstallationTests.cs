using Kot.Core;
namespace Kot.Tests;

public static class InstallationTests
{
    public static void Run(Action<bool, string> check, string folder)
    {
        string root = Path.Combine(folder, "installation-cleanup");
        Directory.CreateDirectory(Path.Combine(root, "licenses"));
        File.WriteAllText(Path.Combine(root, "README.md"), "old installed guide");
        File.WriteAllText(Path.Combine(root, "SECURITY-AUDIT.md"), "old report");
        File.WriteAllText(Path.Combine(root, "LICENSE.txt"), "old root notice");
        File.WriteAllText(Path.Combine(root, "licenses", "kot-LICENSE.txt"), "new notice");
        File.WriteAllText(Path.Combine(root, "licenses", "README.md"), "dependency notice");
        File.WriteAllText(Path.Combine(root, "my-notes.txt"), "user data");
        File.WriteAllText(Path.Combine(root, "profile.json"), "user profile");
        File.WriteAllText(Path.Combine(root, "release.json"), "signature manifest");
        File.WriteAllText(Path.Combine(root, "release.sig"), "signature");
        Directory.CreateDirectory(Path.Combine(root, "README.txt"));
        string external = Path.GetFullPath(Path.Combine(folder, "external-notice.txt"));
        File.WriteAllText(external, "external data");
        File.CreateSymbolicLink(Path.Combine(root, "THIRD-PARTY.txt"), external);
        InstallationCleanup.RemoveLegacyDocuments(root);
        check(!File.Exists(Path.Combine(root, "README.md")) && !File.Exists(Path.Combine(root, "SECURITY-AUDIT.md")) && !File.Exists(Path.Combine(root, "LICENSE.txt")), "upgrade removes known obsolete root documents");
        check(File.ReadAllText(Path.Combine(root, "licenses", "kot-LICENSE.txt")) == "new notice" && File.ReadAllText(Path.Combine(root, "licenses", "README.md")) == "dependency notice", "upgrade preserves all notices in licenses including Markdown");
        check(File.ReadAllText(Path.Combine(root, "my-notes.txt")) == "user data" && File.ReadAllText(Path.Combine(root, "profile.json")) == "user profile" && File.Exists(Path.Combine(root, "release.json")) && File.Exists(Path.Combine(root, "release.sig")), "cleanup preserves unrelated files, profiles and signature metadata");
        check(Directory.Exists(Path.Combine(root, "README.txt")) && File.Exists(Path.Combine(root, "THIRD-PARTY.txt")) && File.ReadAllText(external) == "external data", "cleanup leaves matching directories and symlinks untouched");
        InstallationCleanup.RemoveLegacyDocuments(root);
        check(File.ReadAllText(external) == "external data", "cleanup is safe to repeat");
        Directory.Delete(root, true); File.Delete(external);
    }
}
