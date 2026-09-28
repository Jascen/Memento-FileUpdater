using System.Text.Json;
using FileUpdaterClient.Updating;

namespace FileUpdaterClient.Tests;

public class LocalFilesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "updater-root");

    [Theory]
    [InlineData("map0.mul")]
    [InlineData("maps/map0.mul")]
    public void AcceptsPathsInsideInstallFolder(string name)
    {
        Assert.True(LocalFiles.TryGetLocalPath(Root, name, out var path));
        Assert.StartsWith(Root, path);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("maps/../../outside.txt")]
    [InlineData("/etc/passwd")]
    public void RejectsPathsOutsideInstallFolder(string name)
    {
        Assert.False(LocalFiles.TryGetLocalPath(Root, name, out _));
    }

    [Fact]
    public void ComputesLowercaseMd5()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "hello");
        Assert.Equal(FakeServer.Md5("hello"), LocalFiles.ComputeMd5(file));
        File.Delete(file);
    }

    [Fact]
    public void FileEntryReadsServerJson()
    {
        var entries = JsonSerializer.Deserialize<FileEntry[]>("""[{"name":"a.mul","md5":"abc"}]""")!;
        Assert.Equal("a.mul", entries[0].Name);
        Assert.Equal("abc", entries[0].Md5);
    }
}
