using System.IO.Abstractions.TestingHelpers;
using FileUpdaterClient.Updating;

namespace FileUpdaterClient.Tests.Updating;

//Runs against an in-memory file system. Unzipping is faked by writing the package's files into the staging folder
public class LauncherSwapTests
{
    private static readonly string AppDirectory = MockUnixSupport.Path(@"C:\app");
    private static readonly string Zip = MockUnixSupport.Path(@"C:\downloads\launcher.zip");
    private static readonly string ExePath = MockUnixSupport.Path(@"C:\app\Launcher.exe");

    private readonly MockFileSystem _fileSystem = new();

    private LauncherSwap CreateSwap(Dictionary<string, string> packageFiles, Exception? extractError = null) =>
        new(_fileSystem, (_, folder) =>
        {
            if (extractError != null) throw extractError;
            foreach (var (name, content) in packageFiles)
                _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(folder, name), content);
        });

    [Fact]
    public void ReplacesTheInstalledFilesAndCleansUp()
    {
        //Arrange
        _fileSystem.AddFile(ExePath, new MockFileData("old exe"));
        _fileSystem.AddFile(_fileSystem.Path.Combine(AppDirectory, "libSkiaSharp.dll"), new MockFileData("old lib"));
        var swap = CreateSwap(new() { ["Launcher.exe"] = "new exe", ["libSkiaSharp.dll"] = "new lib", ["extra.dll"] = "extra" });

        //Act
        swap.Apply(Zip, AppDirectory, ExePath);

        //Assert
        Assert.Equal("new exe", _fileSystem.File.ReadAllText(ExePath));
        Assert.Equal("new lib", _fileSystem.File.ReadAllText(_fileSystem.Path.Combine(AppDirectory, "libSkiaSharp.dll")));
        Assert.Equal("extra", _fileSystem.File.ReadAllText(_fileSystem.Path.Combine(AppDirectory, "extra.dll")));
        Assert.DoesNotContain(_fileSystem.AllDirectories, d => d.Contains("launcher-staging-"));
    }

    [Fact]
    public void LeavesTheInstallUntouchedWhenTheZipCantBeUnpacked()
    {
        //Arrange
        _fileSystem.AddFile(ExePath, new MockFileData("old exe"));
        var swap = CreateSwap(new(), new InvalidDataException("bad zip"));

        //Act
        var apply = () => swap.Apply(Zip, AppDirectory, ExePath);

        //Assert
        Assert.Throws<InvalidDataException>(apply);
        Assert.Equal("old exe", _fileSystem.File.ReadAllText(ExePath));
        Assert.DoesNotContain(_fileSystem.AllDirectories, d => d.Contains("launcher-staging-"));
    }

    [Fact]
    public void RejectsAPackageWithoutTheLauncherExe()
    {
        //Arrange
        _fileSystem.AddFile(ExePath, new MockFileData("old exe"));
        var swap = CreateSwap(new() { ["other.dll"] = "x" });

        //Act
        var apply = () => swap.Apply(Zip, AppDirectory, ExePath);

        //Assert
        Assert.Throws<InvalidDataException>(apply);
        Assert.Equal("old exe", _fileSystem.File.ReadAllText(ExePath));
        Assert.False(_fileSystem.File.Exists(_fileSystem.Path.Combine(AppDirectory, "other.dll"))); //Nothing was copied
    }

    [Fact]
    public void RejectsAnEmptyPackage()
    {
        //Arrange
        _fileSystem.AddFile(ExePath, new MockFileData("old exe"));
        var swap = CreateSwap(new());

        //Act
        var apply = () => swap.Apply(Zip, AppDirectory, ExePath);

        //Assert
        Assert.Throws<InvalidDataException>(apply);
        Assert.Equal("old exe", _fileSystem.File.ReadAllText(ExePath));
    }
}
