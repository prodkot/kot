using System.Text.Json;
namespace Kot.Core;

public static class InstallationCleanup
{
    // This exact list is also used by the Setup compiler. Never match *.txt or *.md:
    // unrelated user files and every notice in licenses/ must survive an upgrade.
    public static readonly IReadOnlyList<string> LegacyDocuments = JsonSerializer.Deserialize<string[]>(
        typeof(InstallationCleanup).Assembly.GetManifestResourceStream("Kot.Core.LegacyDocuments.json")!)!;

    public static void RemoveLegacyDocuments(string root)
    {
        UpdateTransaction.SafePath(root);
        foreach (string name in LegacyDocuments)
        {
            string path = Path.Combine(root, name);
            try
            {
                UpdateTransaction.SafePath(path);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { } // Locked files can be retried on the next launch.
            catch (UnauthorizedAccessException) { }
            catch (UserError) { } // Leave links and junctions alone.
        }
    }
}
