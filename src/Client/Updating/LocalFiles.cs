using System.IO.Abstractions;
using System.Security.Cryptography;

namespace FileUpdaterClient.Updating;

//File system helpers for the install folder. Goes through IFileSystem so tests can use an in-memory one
public class LocalFiles(IFileSystem fileSystem)
{
    //Resolves a server-provided file name to a local path, rejecting any name that would land outside the install folder
    public static bool TryGetLocalPath(string installPath, string name, out string fullPath)
    {
        var baseDirectory = Path.GetFullPath(installPath);
        fullPath = Path.GetFullPath(name, baseDirectory);
        var root = Path.TrimEndingDirectorySeparator(baseDirectory) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    public bool Exists(string fileName) => fileSystem.File.Exists(fileName);

    public string ComputeMd5(string fileName)
    {
        using var md5 = MD5.Create();
        using var stream = fileSystem.File.OpenRead(fileName);
        return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
    }

    public void EnsureDirectory(string filePath)
    {
        string? dirPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dirPath) && !fileSystem.Directory.Exists(dirPath))
        {
            fileSystem.Directory.CreateDirectory(dirPath);
        }
    }

    public Stream Create(string fileName) => fileSystem.File.Create(fileName);

    public void Move(string source, string destination) => fileSystem.File.Move(source, destination, overwrite: true);

    public void DeleteIfExists(string fileName)
    {
        if (fileSystem.File.Exists(fileName))
            fileSystem.File.Delete(fileName);
    }
}
