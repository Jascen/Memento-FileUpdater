using System.IO.Abstractions;
using System.Security.Cryptography;

namespace FileUpdaterPackages;

//Scans a packages folder and describes the newest package for each role and platform.
//ignored are zips that don't follow the naming convention, so the signer can tell the admin about them.
public record BuildResult(PackageManifest Manifest, List<string> Ignored);

public static class ManifestBuilder
{
    public static BuildResult Build(IFileSystem fileSystem, string packagesDirectory, DateTimeOffset generated)
    {
        var newest = new Dictionary<(string Role, string Rid), (Version Version, string File)>();
        var ignored = new List<string>();

        foreach (var path in fileSystem.Directory.GetFiles(packagesDirectory, "*.zip"))
        {
            var name = fileSystem.Path.GetFileName(path);
            if (!PackageFileName.TryParse(name, out var role, out var version, out var rid))
            {
                ignored.Add(name);
                continue;
            }

            var key = (role, rid);
            if (!newest.TryGetValue(key, out var current) || version > current.Version)
                newest[key] = (version, name);
        }

        var packages = newest
            .OrderBy(p => p.Key.Role).ThenBy(p => p.Key.Rid)
            .Select(p => Describe(fileSystem, packagesDirectory, p.Key.Role, p.Key.Rid, p.Value.Version, p.Value.File))
            .ToList();

        ignored.Sort(StringComparer.OrdinalIgnoreCase);
        return new BuildResult(new PackageManifest(generated, packages), ignored);
    }

    private static PackageEntry Describe(IFileSystem fileSystem, string directory, string role, string rid, Version version, string file)
    {
        var path = fileSystem.Path.Combine(directory, file);
        using var stream = fileSystem.File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new PackageEntry(role, version.ToString(), rid, file, hash, fileSystem.FileInfo.New(path).Length);
    }
}
