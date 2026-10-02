using FileUpdaterClient.Updating;

namespace FileUpdaterClient.Tests.Updating;

public class IgnoreRulesTests
{
    [Theory]
    [InlineData("map0.mul", "map0.mul", "map0.mul")] //A bare name matches at the top
    [InlineData("map0.mul", "maps/map0.mul", "maps/map0.mul")] //and in any folder
    [InlineData("MAP0.MUL", "maps/map0.mul", "maps/map0.mul")] //Case doesn't matter
    [InlineData("*.cfg", "Data/macros.cfg", "Data/macros.cfg")]
    [InlineData("map?.mul", "map7.mul", "map7.mul")]
    [InlineData("map[0-2].mul", "map1.mul", "map1.mul")]
    [InlineData("map[!0-2].mul", "map5.mul", "map5.mul")]
    [InlineData("Music/", "Music/intro.mp3", "Music/")] //A folder is reported once, not file by file
    [InlineData("Music", "Data/Music/intro.mp3", "Data/Music/")]
    [InlineData("/map0.mul", "map0.mul", "map0.mul")] //A leading slash ties it to the top of the install folder
    [InlineData("Data/*.cfg", "Data/a.cfg", "Data/a.cfg")]
    [InlineData("**/skins", "a/b/skins/x.png", "a/b/skins/")]
    [InlineData("Data/**/old.mul", "Data/x/y/old.mul", "Data/x/y/old.mul")]
    [InlineData("Data/**/old.mul", "Data/old.mul", "Data/old.mul")]
    [InlineData("Data/**", "Data/a.mul", "Data/a.mul")] //Everything inside
    [InlineData("Data/**", "Data/x/a.mul", "Data/x/")]
    [InlineData(@"\#notes.txt", "#notes.txt", "#notes.txt")]
    public void IgnoresMatchingNames(string pattern, string name, string reported)
    {
        //Arrange
        var rules = IgnoreRules.Parse(pattern);

        //Act
        var match = rules.Match(name);

        //Assert
        Assert.Equal(reported, match);
    }

    [Theory]
    [InlineData("map0.mul", "map0.mul.bak")]
    [InlineData("map0.mul", "map00.mul")]
    [InlineData("/map0.mul", "maps/map0.mul")] //Only at the top
    [InlineData("Data/*.cfg", "Data/sub/a.cfg")] //* stays within one folder
    [InlineData("Data/*.cfg", "Other/Data/a.cfg")] //A slash in the middle ties it to the top too
    [InlineData("Music/", "Music")] //A trailing slash only matches folders
    [InlineData("*.cfg", "cfg/readme.txt")]
    [InlineData("# map0.mul", "map0.mul")] //A comment
    [InlineData("", "map0.mul")]
    public void LeavesOtherNamesAlone(string pattern, string name)
    {
        //Arrange
        var rules = IgnoreRules.Parse(pattern);

        //Act
        var match = rules.Match(name);

        //Assert
        Assert.Null(match);
    }

    [Fact]
    public void ALaterLineCanBringBackAFile()
    {
        //Arrange
        var rules = IgnoreRules.Parse("*.mul\r\n!map0.mul\n");

        //Act
        var map0 = rules.Match("map0.mul");
        var map1 = rules.Match("map1.mul");

        //Assert
        Assert.Null(map0);
        Assert.Equal("map1.mul", map1);
    }

    [Fact]
    public void AFileInsideAnIgnoredFolderCantBeBroughtBack()
    {
        //Arrange
        var rules = IgnoreRules.Parse("Music/\n!Music/theme.mp3");

        //Act
        var match = rules.Match("Music/theme.mp3");

        //Assert
        Assert.Equal("Music/", match); //Same as git
    }

    [Fact]
    public void AnEmptyListIgnoresNothing()
    {
        //Arrange
        var rules = IgnoreRules.Parse("\n# just a comment\n   \n");

        //Act
        var match = rules.Match("map0.mul");

        //Assert
        Assert.True(rules.IsEmpty);
        Assert.Null(match);
    }
}
