namespace FileUpdaterServer.Tests;

public class FileRequestHandlerTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "served"));

    [Theory]
    [InlineData("map0.mul")]
    [InlineData("maps/map1.mul")]
    [InlineData("notes..txt")] //Dots inside a name aren't a parent folder
    [InlineData("maps/..hidden")]
    public void ResolvesFilesInsideTheRoot(string relativePath)
    {
        //Act
        var resolved = FileRequestHandler.TryResolve(Root, relativePath, traversalProtection: true, out var fullPath);

        //Assert
        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, relativePath)), fullPath);
    }

    [Theory]
    [InlineData("../secret.txt", true)]
    [InlineData("maps/../../secret.txt", true)]
    [InlineData("../secret.txt", false)] //The normalized check still catches it when the simple one is off
    [InlineData("maps/../../secret.txt", false)]
    public void RejectsPathsThatLeaveTheRoot(string relativePath, bool traversalProtection)
    {
        //Act
        var resolved = FileRequestHandler.TryResolve(Root, relativePath, traversalProtection, out var fullPath);

        //Assert
        Assert.False(resolved);
        Assert.Empty(fullPath);
    }

    [Fact]
    public void RejectsAbsolutePaths()
    {
        //Arrange
        var outside = Path.GetFullPath(Path.Combine(Root, "..", "secret.txt"));

        //Act
        var resolved = FileRequestHandler.TryResolve(Root, outside, traversalProtection: true, out _);

        //Assert
        Assert.False(resolved);
    }

    [Fact]
    public void RejectsASiblingFolderThatOnlySharesTheRootsPrefix()
    {
        //Act
        var resolved = FileRequestHandler.TryResolve(Root, "../served-evil/x.txt", traversalProtection: false, out _);

        //Assert
        Assert.False(resolved);
    }
}
