using System.Security.Cryptography;

namespace FileUpdaterClient.Updating;

//File system helpers for the install folder
public static class LocalFiles
{
    //Resolves a server-provided file name to a local path, rejecting any name that would land outside the install folder
    public static bool TryGetLocalPath(string installPath, string name, out string fullPath)
    {
        var baseDirectory = Path.GetFullPath(installPath);
        fullPath = Path.GetFullPath(name, baseDirectory);
        var root = Path.TrimEndingDirectorySeparator(baseDirectory) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    public static string ComputeMd5(string fileName)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(fileName);
        return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
    }

    public static void EnsureDirectory(string filePath)
    {
        string? dirPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dirPath) && !Directory.Exists(dirPath))
        {
            Directory.CreateDirectory(dirPath);
        }
    }
}
