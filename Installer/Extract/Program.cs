using Kot.Installer;

if (args.Length != 5 || args[0] != "--extract") return 2;
try
{
    // The embedded helper always uses the publisher key pinned in the client.
    SetupExtraction.Extract(args[1], args[2], args[3]);
    return 0;
}
catch (Exception error)
{
    try { File.WriteAllText(args[4], error.Message[..Math.Min(error.Message.Length, 1500)], System.Text.Encoding.Unicode); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
    return 1;
}
