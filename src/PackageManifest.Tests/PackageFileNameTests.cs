namespace FileUpdaterPackages.Tests;

public class PackageFileNameTests
{
    [Theory]
    [InlineData("launcher-1.2.0.win-x64.zip", "launcher", "1.2.0", "win-x64")]
    [InlineData("client-3.4.0.1.linux-x64.zip", "client", "3.4.0.1", "linux-x64")]
    [InlineData("Launcher-10.0.osx-arm64.ZIP", "launcher", "10.0", "osx-arm64")]
    public void ParsesRoleVersionAndPlatform(string fileName, string role, string version, string rid)
    {
        //Act
        var parsed = PackageFileName.TryParse(fileName, out var actualRole, out var actualVersion, out var actualRid);

        //Assert
        Assert.True(parsed);
        Assert.Equal(role, actualRole);
        Assert.Equal(Version.Parse(version), actualVersion);
        Assert.Equal(rid, actualRid);
    }

    [Theory]
    [InlineData("launcher.win-x64.zip")] //No version
    [InlineData("launcher-1.win-x64.zip")] //One-part version
    [InlineData("game-1.2.0.win-x64.zip")] //Unknown role
    [InlineData("launcher-1.2.0.win-x64.exe")]
    [InlineData("launcher-1.2.0.zip")] //No platform
    [InlineData("../launcher-1.2.0.win-x64.zip")]
    [InlineData("sub/launcher-1.2.0.win-x64.zip")]
    public void RejectsNamesOutsideTheConvention(string fileName)
    {
        //Act
        var parsed = PackageFileName.TryParse(fileName, out _, out _, out _);

        //Assert
        Assert.False(parsed);
    }
}
