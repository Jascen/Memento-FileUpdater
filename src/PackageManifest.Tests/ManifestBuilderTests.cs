using System.IO.Abstractions.TestingHelpers;
using System.Text;

namespace FileUpdaterPackages.Tests;

public class ManifestBuilderTests
{
    private static readonly string Root = MockUnixSupport.Path(@"C:\packages");
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly MockFileSystem _fileSystem = new();

    public ManifestBuilderTests() => _fileSystem.AddDirectory(Root);

    [Fact]
    public void DescribesEachPackageWithSha256AndSize()
    {
        //Arrange
        AddFile("launcher-1.0.0.win-x64.zip", "hello");

        //Act
        var result = ManifestBuilder.Build(_fileSystem, Root, Now);

        //Assert
        var entry = Assert.Single(result.Manifest.Packages);
        Assert.Equal(new PackageEntry("launcher", "1.0.0", "win-x64", "launcher-1.0.0.win-x64.zip",
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", 5), entry);
        Assert.Equal(Now, result.Manifest.Generated);
    }

    [Fact]
    public void ListsOnlyTheNewestVersionOfEachRoleAndPlatform()
    {
        //Arrange
        AddFile("launcher-1.9.0.win-x64.zip", "old");
        AddFile("launcher-1.10.0.win-x64.zip", "new"); //Compared as versions, not text
        AddFile("launcher-0.5.0.linux-x64.zip", "other platform");
        AddFile("client-3.0.0.win-x64.zip", "client");

        //Act
        var result = ManifestBuilder.Build(_fileSystem, Root, Now);

        //Assert
        Assert.Equal(
            ["client 3.0.0 win-x64", "launcher 0.5.0 linux-x64", "launcher 1.10.0 win-x64"],
            result.Manifest.Packages.Select(p => $"{p.Role} {p.Version} {p.Rid}"));
    }

    [Fact]
    public void ReportsZipsThatDontFollowTheNamingConvention()
    {
        //Arrange
        AddFile("launcher-1.0.0.win-x64.zip", "a");
        AddFile("notes.zip", "b");
        AddFile("readme.txt", "c");

        //Act
        var result = ManifestBuilder.Build(_fileSystem, Root, Now);

        //Assert
        Assert.Single(result.Manifest.Packages);
        Assert.Equal(["notes.zip"], result.Ignored);
    }

    [Fact]
    public void FindLooksUpByRoleAndPlatform()
    {
        //Arrange
        AddFile("launcher-1.0.0.win-x64.zip", "a");
        var manifest = ManifestBuilder.Build(_fileSystem, Root, Now).Manifest;

        //Act
        var found = manifest.Find(PackageRole.Launcher, "win-x64");
        var missing = manifest.Find(PackageRole.Client, "win-x64");

        //Assert
        Assert.Equal("1.0.0", found?.Version);
        Assert.Null(missing);
    }

    [Fact]
    public void ManifestSurvivesAJsonRoundTrip()
    {
        //Arrange
        AddFile("client-3.0.0.win-x64.zip", "client");
        var manifest = ManifestBuilder.Build(_fileSystem, Root, Now).Manifest;

        //Act
        var parsed = PackageManifest.Parse(manifest.ToJsonBytes());

        //Assert
        Assert.Equal(manifest.Generated, parsed.Generated);
        Assert.Equal(manifest.Packages, parsed.Packages);
    }

    private void AddFile(string name, string content) =>
        _fileSystem.AddFile(Path.Combine(Root, name), new MockFileData(Encoding.UTF8.GetBytes(content)));
}
