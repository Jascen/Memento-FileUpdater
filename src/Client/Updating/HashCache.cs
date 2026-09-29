using System.Collections.Concurrent;
using System.Text.Json;

namespace FileUpdaterClient.Updating;

//Remembers each local file's MD5 with the size and modified time it had when hashed, so unchanged files aren't
//re-hashed on every check. Saved in the install folder, so it moves with the game files.
public class HashCache
{
    public const string FileName = ".launcher-hashes.json";

    private readonly LocalFiles _localFiles;
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _changed;

    public HashCache(LocalFiles localFiles, string installPath)
    {
        _localFiles = localFiles;
        _cachePath = Path.Combine(installPath, FileName);
        try
        {
            var json = _localFiles.ReadAllTextIfExists(_cachePath);
            if (json == null) return;

            foreach (var (name, entry) in JsonSerializer.Deserialize<Dictionary<string, Entry>>(json) ?? new())
                _entries[name] = entry;
        }
        catch (Exception e)
        {
            //A damaged cache only costs a full re-hash
            Console.WriteLine($"Ignoring hash cache {_cachePath}: {e.Message}");
        }
    }

    //MD5 of the file at fullPath, from the cache when its size and modified time haven't changed since it was hashed
    public string GetMd5(string name, string fullPath)
    {
        var size = _localFiles.Length(fullPath);
        var modified = _localFiles.LastWriteTimeUtc(fullPath).Ticks;
        if (_entries.TryGetValue(name, out var cached) && cached.Size == size && cached.Modified == modified)
            return cached.Md5;

        var md5 = _localFiles.ComputeMd5(fullPath);
        Set(name, fullPath, md5);
        return md5;
    }

    //Records a hash already known to be right, e.g. a file that was just downloaded and checked
    public void Set(string name, string fullPath, string md5)
    {
        _entries[name] = new Entry(_localFiles.Length(fullPath), _localFiles.LastWriteTimeUtc(fullPath).Ticks, md5);
        _changed = true;
    }

    public void Save()
    {
        if (!_changed) return;
        try
        {
            _localFiles.WriteAllText(_cachePath, JsonSerializer.Serialize(new Dictionary<string, Entry>(_entries)));
            _changed = false;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not save hash cache {_cachePath}: {e.Message}");
        }
    }

    public record Entry(long Size, long Modified, string Md5);
}
