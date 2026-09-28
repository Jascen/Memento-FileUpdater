using FileUpdaterClient.Updating;

namespace FileUpdaterClient.Tests.Updating;

public class UnitsTests
{
    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10 * 1024 * 1024, "10 MB")]
    [InlineData(3.5 * 1024 * 1024 * 1024, "3.5 GB")]
    public void FormatsBytes(double bytes, string expected)
    {
        //Act
        var text = Units.Bytes(bytes);

        //Assert
        Assert.Equal(expected, text);
    }

    [Fact]
    public void FormatsSpeedInMegabytes()
    {
        //Act
        var text = Units.Speed(12.3 * 1024 * 1024);

        //Assert
        Assert.Equal("12 MB/s", text);
    }

    [Theory]
    [InlineData(44.2, "45s")]
    [InlineData(200, "3m 20s")]
    [InlineData(3900, "1h 5m")]
    public void FormatsDurations(double seconds, string expected)
    {
        //Act
        var text = Units.Duration(TimeSpan.FromSeconds(seconds));

        //Assert
        Assert.Equal(expected, text);
    }
}
