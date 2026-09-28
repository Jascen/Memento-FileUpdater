using System.IO.Abstractions;
using System.Security.Cryptography;

namespace FileUpdaterServer;

//Builds the file list served at GET /. Remembers each file's size, modified time and MD5 between runs,
//so only new or changed files are hashed, and leaves out files that are still being copied in
public class FileListBuilder
{
    private readonly IFileSystem _fileSystem;
    private readonly string _filesDirectory;
    private readonly TimeSpan _settleTime;
    private readonly TimeProvider _time;
    private Dictionary<string, KnownFile> _known = new(StringComparer.Ordinal);

    public FileListBuilder(IFileSystem fileSystem, string filesDirectory, TimeSpan settleTime, TimeProvider time)
    {
        _fileSystem = fileSystem;
        _filesDirectory = filesDirectory;
        _settleTime = settleTime;
        _time = time;
    }

    public async Task<FileListResult> BuildAsync(CancellationToken cancellationToken)
    {
        var entries = new List<FileEntry>();
        var known = new Dictionary<string, KnownFile>(StringComparer.Ordinal);
        var skipped = new List<string>();
        var errors = new List<(string Name, Exception Error)>();
        var hashed = 0;
        var now = _time.GetUtcNow().UtcDateTime;

        foreach (var path in _fileSystem.Directory.EnumerateFiles(_filesDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            //Clients use forward slashes on every platform
            var name = _fileSystem.Path.GetRelativePath(_filesDirectory, path).Replace(_fileSystem.Path.DirectorySeparatorChar, '/');

            try
            {
                var info = _fileSystem.FileInfo.New(path);
                var size = info.Length;
                var modified = info.LastWriteTimeUtc;

                //Written to moments ago: probably still being copied in
                var age = now - modified;
                if (age >= TimeSpan.Zero && age < _settleTime)
                {
                    skipped.Add(name);
                    continue;
                }

                if (!_known.TryGetValue(name, out var file) || file.Size != size || file.Modified != modified)
                {
                    var md5 = await TryHashAsync(path, cancellationToken);

                    //Open for writing elsewhere, or changed while it was being hashed
                    info.Refresh();
                    if (md5 == null || info.Length != size || info.LastWriteTimeUtc != modified)
                    {
                        skipped.Add(name);
                        continue;
                    }

                    file = new KnownFile(size, modified, md5);
                    hashed++;
                }

                known[name] = file;
                entries.Add(new FileEntry { name = name, md5 = file.Md5, size = file.Size });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add((name, ex));
            }
        }

        _known = known;
        entries.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return new FileListResult(entries, hashed, skipped, errors);
    }

    //Returns null when another program has the file open for writing
    private async Task<string?> TryHashAsync(string path, CancellationToken cancellationToken)
    {
        Stream stream;
        try
        {
            stream = _fileSystem.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException)
        {
            return null;
        }

        await using (stream)
        {
            var hash = await MD5.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexStringLower(hash);
        }
    }

    private record KnownFile(long Size, DateTime Modified, string Md5);
}

//Skipped files were still being written and are left out until a later run; Errors couldn't be read
public record FileListResult(
    List<FileEntry> Entries,
    int Hashed,
    List<string> Skipped,
    List<(string Name, Exception Error)> Errors);

public class FileEntry
{
    public string name { get; set; } = string.Empty;
    public string md5 { get; set; } = string.Empty;
    public long size { get; set; }
}
