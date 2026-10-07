using System.IO.Compression;
using Kot.Core;

if (args.Length != 2) throw new ArgumentException("Pass the archive and expected release version.");
using var archive = ZipFile.OpenRead(args[0]);
var manifest = ReleasePackage.Validate(archive);
if (manifest.Version != args[1]) throw new InvalidOperationException("Package and release versions disagree.");
Console.WriteLine($"Client accepted signed release {manifest.Version}: {manifest.Files.Count} file hashes verified.");
