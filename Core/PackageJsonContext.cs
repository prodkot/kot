using System.Text.Json.Serialization;
namespace Kot.Core;

// Explicit metadata lets the small native installer share the client's validators.
// Keep PascalCase journal output: an older updater must still be able to roll it back.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SetupPackage.Manifest))]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(List<UpdateTransaction.Change>))]
internal partial class PackageJsonContext : JsonSerializerContext;
