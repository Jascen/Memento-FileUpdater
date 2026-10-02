using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using FileUpdaterClient.Tests.Fakes;
using FileUpdaterClient.Updating;

namespace FileUpdaterClient.Tests.Updating;

public class LocalFilesTests
{
    private static readonly string Root = MockUnixSupport.Path(@"C:\game");

    [Theory]
    [InlineData("map0.mul")]
    [InlineData("maps/map0.mul")]
    public void AcceptsPathsInsideInstallFolder(string name)
    {
        //Act
        var accepted = LocalFiles.TryGetLocalPath(Root, name, out var path);

        //Assert
        Assert.True(accepted);
        Assert.StartsWith(Root, path);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("maps/../../outside.txt")]
    [InlineData("/etc/passwd")]
    public void RejectsPathsOutsideInstallFolder(string name)
    {
        //Act
        var accepted = LocalFiles.TryGetLocalPath(Root, name, out _);

        //Assert
        Assert.False(accepted);
    }

    [Fact]
    public void ComputesLowercaseMd5()
    {
        //Arrange
        var file = MockUnixSupport.Path(@"C:\game\hello.txt");
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [file] = new("hello") });
        var localFiles = new LocalFiles(fileSystem);

        //Act
        var md5 = localFiles.ComputeMd5(file);

        //Assert
        Assert.Equal(FakeServer.Md5("hello"), md5);
    }

    [Fact]
    public void FileEntryReadsServerJson()
    {
        //Arrange
        const string json = """[{"name":"a.mul","md5":"abc"}]""";

        //Act
        var entries = JsonSerializer.Deserialize<FileEntry[]>(json)!;

        //Assert
        var entry = Assert.Single(entries);
        Assert.Equal("a.mul", entry.Name);
        Assert.Equal("abc", entry.Md5);
    }

    [Fact]
    public void FileEntryReadsSizeWhenPresent()
    {
        //Arrange
        const string json = """[{"name":"a.mul","md5":"abc","size":42}]""";

        //Act
        var entries = JsonSerializer.Deserialize<FileEntry[]>(json)!;

        //Assert
        Assert.Equal(42, Assert.Single(entries).Size);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)] //ERROR_SHARING_VIOLATION
    [InlineData(unchecked((int)0x80070021), true)] //ERROR_LOCK_VIOLATION
    [InlineData(unchecked((int)0x80070005), false)] //Access denied
    public void RecognizesFilesLockedByAnotherProgram(int hresult, bool expected)
    {
        //Arrange
        var exception = new IOException("in use", hresult);

        //Act
        var locked = LocalFiles.IsLocked(exception);

        //Assert
        Assert.Equal(expected, locked);
    }
}
