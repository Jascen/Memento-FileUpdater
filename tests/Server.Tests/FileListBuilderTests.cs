using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using FileUpdaterServer;
using Microsoft.Extensions.Time.Testing;

namespace FileUpdaterServer.Tests;

public class FileListBuilderTests
{
    private static readonly string Root = MockUnixSupport.Path(@"C:\files");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FileListBuilder _builder;

    public FileListBuilderTests()
    {
        _fileSystem.AddDirectory(Root);
        _builder = new FileListBuilder(_fileSystem, Root, SettleTime, _time);
    }

    [Fact]
    public async Task ListsNameMd5AndSizeOfEveryFile()
    {
        //Arrange
        AddFile("map0.mul", "hello", Now.AddMinutes(-1));
        AddFile(@"maps\map1.mul", "abc", Now.AddMinutes(-1));

        //Act
        var result = await _builder.BuildAsync(CancellationToken.None);

        //Assert
        Assert.Collection(result.Entries,
            e =>
            {
                Assert.Equal("map0.mul", e.name);
                Assert.Equal("5d41402abc4b2a76b9719d911017c592", e.md5);
                Assert.Equal(5, e.size);
            },
            e =>
            {
                Assert.Equal("maps/map1.mul", e.name);
                Assert.Equal("900150983cd24fb0d6963f7d28e17f72", e.md5);
                Assert.Equal(3, e.size);
            });
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task LeavesOutFilesModifiedWithinSettleTime()
    {
        //Arrange
        AddFile("done.mul", "done", Now.AddMinutes(-1));
        AddFile("copying.mul", "half", Now.AddSeconds(-2));

        //Act
        var result = await _builder.BuildAsync(CancellationToken.None);

        //Assert
        Assert.Equal(["done.mul"], result.Entries.Select(e => e.name));
        Assert.Equal(["copying.mul"], result.Skipped);
    }

    [Fact]
    public async Task PublishesFileOnceItHasSettled()
    {
        //Arrange
        AddFile("copying.mul", "whole", Now.AddSeconds(-2));
        await _builder.BuildAsync(CancellationToken.None);
        _time.Advance(SettleTime);

        //Act
        var result = await _builder.BuildAsync(CancellationToken.None);

        //Assert
        Assert.Equal(["copying.mul"], result.Entries.Select(e => e.name));
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task LeavesOutFilesOpenForWriting()
    {
        //Arrange
        AddFile("locked.mul", "half", Now.AddMinutes(-1));
        _fileSystem.GetFile(Path("locked.mul")).AllowedFileShare = FileShare.None;

        //Act
        var result = await _builder.BuildAsync(CancellationToken.None);

        //Assert
        Assert.Empty(result.Entries);
        Assert.Equal(["locked.mul"], result.Skipped);
    }

    [Fact]
    public async Task ReusesHashWhenSizeAndModifiedTimeAreUnchanged()
    {
        //Arrange
        var modified = Now.AddMinutes(-1);
        AddFile("map0.mul", "hello", modified);
        var first = await _builder.BuildAsync(CancellationToken.None);
        AddFile("map0.mul", "HELLO", modified);

        //Act
        var result = await _builder.BuildAsync(CancellationToken.None);

        //Assert
        Assert.Equal(first.Entries[0].md5, result.Entries[0].md5);
        Assert.Equal(0, result.Hashed);
    }

    [Fact]
    public async Task RehashesWhenModifiedTimeChanges()
    {
        //Arrange
        AddFile("map0.mul", "hello", Now.AddMinutes(-2));
        await _builder.BuildAsync(CancellationToken.None);
        AddFile("map0.mul", "HELLO", Now.AddMinutes(-1));

        //Act
        var result = await _builder.BuildAsync(CancellationToken.None);

        //Assert
        Assert.Equal("eb61eead90e3b899c6bcbe27ac581660", result.Entries[0].md5);
        Assert.Equal(1, result.Hashed);
    }

    [Fact]
    public async Task DropsDeletedFiles()
    {
        //Arrange
        AddFile("map0.mul", "hello", Now.AddMinutes(-1));
        AddFile("old.mul", "old", Now.AddMinutes(-1));
        await _builder.BuildAsync(CancellationToken.None);
        _fileSystem.File.Delete(Path("old.mul"));

        //Act
        var result = await _builder.BuildAsync(CancellationToken.None);

        //Assert
        Assert.Equal(["map0.mul"], result.Entries.Select(e => e.name));
    }

    [Fact]
    public void SerializesWithLowercaseNames()
    {
        //Arrange
        var entries = new List<FileEntry> { new() { name = "map0.mul", md5 = "abc", size = 5 } };

        //Act
        var json = JsonSerializer.Serialize(entries);

        //Assert
        Assert.Equal("""[{"name":"map0.mul","md5":"abc","size":5}]""", json);
    }

    private static string Path(string name) => System.IO.Path.Combine(Root, MockUnixSupport.Path(name));

    private void AddFile(string name, string content, DateTimeOffset modified)
    {
        _fileSystem.AddFile(Path(name), new MockFileData(content) { LastWriteTime = modified.UtcDateTime });
    }
}
