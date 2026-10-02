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

    [Theory]
    [InlineData("TazUO Launcher/TazUOLauncher.exe", true)]
    [InlineData("TAZUO LAUNCHER/TazUOLauncher.exe", true)]
    [InlineData("TazUO Launcher", true)]
    [InlineData("maps/../TazUO Launcher/x.dll", true)]
    [InlineData(".launcher-hashes.json", true)]
    [InlineData("../outside.txt", true)] //Outside the install folder isn't the file list's either
    [InlineData("TazUO Launcher Skins/skin.png", false)]
    [InlineData("maps/TazUO Launcher/map0.mul", false)] //Only reserved at the top of the install folder
    [InlineData("map0.mul", false)]
    public void RecognizesReservedPaths(string name, bool expected)
    {
        //Act
        var reserved = LocalFiles.IsReserved(Root, name, ["TazUO Launcher", ".launcher-hashes.json"]);

        //Assert
        Assert.Equal(expected, reserved);
    }

    [Theory]
    [InlineData("TAZUOL~1/TazUOLauncher.exe", true)] //8.3 short name of "TazUO Launcher"
    [InlineData("TA1F2C~1/x.dll", true)]
    [InlineData("maps/MAPFIL~1.MUL", true)]
    [InlineData("TazUO Launcher::$INDEX_ALLOCATION/x.dll", true)]
    [InlineData("map0.mul:stream", true)]
    [InlineData("map0.mul", false)]
    [InlineData("maps/my~map.mul", false)]
    [InlineData("backup~1234567.mul", false)]
    public void RecognizesNamesWindowsOpensAsAnotherFile(string name, bool expected)
    {
        //Act
        var alias = LocalFiles.IsWindowsAlias(name);

        //Assert
        Assert.Equal(expected, alias);
    }

    [Theory]
    [InlineData("https://updates.example.com/", true)]
    [InlineData("https://updates.example.com:8443/launcher", true)]
    [InlineData("http://localhost:8080/", true)] //A server on this machine, for testing
    [InlineData("http://127.0.0.1:8080/", true)]
    [InlineData("http://[::1]:8080/", true)]
    [InlineData("http://updates.example.com/", false)]
    [InlineData("http://192.168.1.10:8080/", false)]
    [InlineData("ftp://updates.example.com/", false)]
    [InlineData("updates.example.com", false)]
    public void OnlyHttpsOrThisMachineIsASecureServer(string url, bool expected)
    {
        //Act
        var secure = FileServerClient.IsSecure(url);

        //Assert
        Assert.Equal(expected, secure);
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
