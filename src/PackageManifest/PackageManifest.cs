using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FileUpdaterPackages;

//The two things a server hosts for download besides game files
public static class PackageRole
{
    public const string Launcher = "launcher"; //The thin updater players run
    public const string TazUO = "tazuo"; //The TazUO launcher and client
}

//One downloadable package. File is the zip's name inside the packages folder, Sha256 is lowercase hex
public record PackageEntry(string Role, string Version, string Rid, string File, string Sha256, long Size);

//What manifest.json holds. It lists only the newest version of each role and platform
public record PackageManifest(DateTimeOffset Generated, List<PackageEntry> Packages)
{
    public const string ManifestFileName = "manifest.json";
    public const string SignatureFileName = "manifest.sig";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public PackageEntry? Find(string role, string rid) =>
        Packages.FirstOrDefault(p => p.Role == role && p.Rid == rid);

    public byte[] ToJsonBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    //Only call this on bytes whose signature has been checked
    public static PackageManifest Parse(byte[] json) =>
        JsonSerializer.Deserialize<PackageManifest>(json, Json)
        ?? throw new InvalidDataException("Manifest was empty.");
}

//Packages are named {role}-{version}.{rid}.zip, e.g. launcher-1.2.0.win-x64.zip or tazuo-3.4.0.win-x64.zip
public static partial class PackageFileName
{
    [GeneratedRegex(@"^(?<role>launcher|tazuo)-(?<version>\d+(?:\.\d+){1,3})\.(?<rid>[a-z][a-z0-9-]*)\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool TryParse(string fileName, out string role, out Version version, out string rid)
    {
        role = rid = string.Empty;
        version = new Version();

        var match = Pattern().Match(fileName);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var parsed)) return false;

        role = match.Groups["role"].Value.ToLowerInvariant();
        version = parsed;
        rid = match.Groups["rid"].Value.ToLowerInvariant();
        return true;
    }

    //True for a bare file name: no folders, on any platform, so it can never point outside the folder it is joined to
    public static bool IsPlain(string fileName) =>
        fileName.Length > 0 && fileName is not ("." or "..") && fileName.IndexOfAny(['/', '\\', '\0']) < 0;

    public static string Format(string role, string version, string rid) => $"{role}-{version}.{rid}.zip";
}
