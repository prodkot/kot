using Kot.Installer;

if (args.Length is not (5 or 6) || args[0] != "--extract") return 2;
try
{
    // The embedded helper always uses the publisher key pinned in the client.
    void Progress(int count, int total)
    {
        if (args.Length != 6) return;
        // Progress reporting is best effort and never affects rollback or success.
        try { File.WriteAllText(args[5], Math.Clamp(count * 94 / Math.Max(1, total), 1, 94).ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    SetupExtraction.Extract(args[1], args[2], args[3], onProgress: Progress);
    return 0;
}
catch (Exception error)
{
    try { File.WriteAllText(args[4], error.Message[..Math.Min(error.Message.Length, 1500)], System.Text.Encoding.Unicode); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
    return 1;
}
