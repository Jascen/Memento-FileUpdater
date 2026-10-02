using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FileUpdaterClient.Updating;

//File system helpers for the install folder. Goes through IFileSystem so tests can use an in-memory one
public partial class LocalFiles(IFileSystem fileSystem)
{
    //Resolves a server-provided file name to a local path, rejecting any name that would land outside the install folder
    public static bool TryGetLocalPath(string installPath, string name, out string fullPath)
    {
        var baseDirectory = Path.GetFullPath(installPath);
        fullPath = Path.GetFullPath(name, baseDirectory);
        var root = Path.TrimEndingDirectorySeparator(baseDirectory) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    //True when a server-provided name is, or is inside, one of reservedPaths (relative to the install folder).
    //Reserved paths belong to something other than the file list, like the folder a signed package is installed into,
    //so the file list must not write there. Case is ignored on every platform, to err on the side of refusing
    public static bool IsReserved(string installPath, string name, IEnumerable<string> reservedPaths)
    {
        if (OperatingSystem.IsWindows() && IsWindowsAlias(name)) return true;
        if (!TryGetLocalPath(installPath, name, out var fullPath)) return true;

        var baseDirectory = Path.GetFullPath(installPath);
        foreach (var reserved in reservedPaths)
        {
            var reservedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reserved, baseDirectory));
            if (fullPath.Equals(reservedPath, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(reservedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    //Windows opens the same file under other spellings: a stream suffix (name::$DATA, folder::$INDEX_ALLOCATION)
    //or an 8.3 short name (TAZUOL~1). Neither is a name a server has a reason to list, and either could reach a reserved path
    public static bool IsWindowsAlias(string name) =>
        name.Contains(':') || name.Split('/', '\\').Any(segment => ShortName().IsMatch(segment));

    [GeneratedRegex(@"^[^.]{1,6}~\d{1,6}(\.[^.]{0,3})?$")]
    private static partial Regex ShortName();

    public bool Exists(string fileName) => fileSystem.File.Exists(fileName);

    public string ComputeMd5(string fileName)
    {
        using var md5 = MD5.Create();
        using var stream = fileSystem.File.OpenRead(fileName);
        return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
    }

    public string ComputeSha256(string fileName)
    {
        using var stream = fileSystem.File.OpenRead(fileName);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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

    public Stream OpenAppend(string fileName) => fileSystem.File.Open(fileName, FileMode.Append, FileAccess.Write);

    public long Length(string fileName) => fileSystem.FileInfo.New(fileName).Length;

    public DateTime LastWriteTimeUtc(string fileName) => fileSystem.File.GetLastWriteTimeUtc(fileName);

    public string? ReadAllTextIfExists(string fileName) =>
        fileSystem.File.Exists(fileName) ? fileSystem.File.ReadAllText(fileName) : null;

    public void WriteAllText(string fileName, string contents) => fileSystem.File.WriteAllText(fileName, contents);

    //True when another program has the file open, e.g. the game is running. Windows reports this as a sharing or lock violation
    public static bool IsLocked(Exception e) => e is IOException { HResult: var hr } && (hr & 0xFFFF) is 32 or 33;

    public void Move(string source, string destination) => fileSystem.File.Move(source, destination, overwrite: true);

    public void DeleteIfExists(string fileName)
    {
        if (fileSystem.File.Exists(fileName))
            fileSystem.File.Delete(fileName);
    }
}
