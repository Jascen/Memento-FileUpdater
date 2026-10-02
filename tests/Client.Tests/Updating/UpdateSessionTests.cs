using System.Net;

namespace FileUpdaterClient.Tests.Updating;

//The session's state between runs: what the window shows comes from here
public class UpdateSessionTests
{
    private const string Url = "http://updates.test/";
    private static readonly string InstallPath = MockUnixSupport.Path(@"C:\game");
    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeServer _server = new();
    private readonly FakeGameClient _client = new();

    public UpdateSessionTests() => _fileSystem.Directory.CreateDirectory(InstallPath);

    private UpdateSession CreateSession(IPackageUpdater? packages = null)
    {
        var localFiles = new LocalFiles(_fileSystem);
        var updates = new UpdateService(new FileServerClient(Url, localFiles, _server), localFiles, InstallPath, packages)
        {
            RetryDelay = _ => TimeSpan.Zero,
        };
        return new UpdateSession(updates, _client, packagesConfigured: packages != null);
    }

    private void WriteLocal(string name, string content) =>
        _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(InstallPath, name), content);

    [Fact]
    public async Task CheckThatFindsUpdatesLeavesThemReadyToDownload()
    {
        //Arrange
        _server.Add("a.mul", "new");
        WriteLocal("a.mul", "old");
        var session = CreateSession();

        //Act
        var result = await session.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.UpdatesReady, result);
        Assert.True(session.DownloadsReady);
        Assert.False(session.IsBusy);
        Assert.False(session.FilesVerified);
    }

    [Fact]
    public async Task DownloadVerifiesTheFiles()
    {
        //Arrange
        _server.Add("a.mul", "new");
        var session = CreateSession();
        await session.CheckAsync();

        //Act
        var result = await session.DownloadAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.False(session.DownloadsReady);
        Assert.True(session.FilesVerified);
        Assert.False(session.RetryReady);
    }

    [Fact]
    public async Task RetryDownloadsWhatTheCheckFinds()
    {
        //Arrange
        _server.Add("a.mul", "new");
        var session = CreateSession();

        //Act
        var result = await session.RetryAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.Equal(["a.mul"], _server.Downloads);
        Assert.True(session.FilesVerified);
    }

    [Fact]
    public async Task FailedCheckOffersRetry()
    {
        //Arrange
        _server.ListStatus = HttpStatusCode.InternalServerError;
        var session = CreateSession();

        //Act
        var result = await session.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Failed, result);
        Assert.True(session.RetryReady);
        Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task StateChangesAreAnnouncedWhenARunStartsAndEnds()
    {
        //Arrange
        var session = CreateSession();
        var busyWhenAnnounced = new List<bool>();
        session.StateChanged += () => busyWhenAnnounced.Add(session.IsBusy);

        //Act
        await session.CheckAsync();

        //Assert
        Assert.Equal([true, false], busyWhenAnnounced);
    }

    [Fact]
    public async Task LauncherUpdateThatIsNotAppliedKeepsPendingDownloads()
    {
        //Arrange
        _server.Add("a.mul", "new");
        var packages = new FakePackageUpdater { Updates = FakePackageUpdater.LauncherPending };
        var session = CreateSession(packages);
        await session.CheckAsync();

        //Act
        var result = await session.UpdateLauncherAsync();

        //Assert
        Assert.Equal(UpdateResult.Failed, result);
        Assert.True(session.DownloadsReady);
        Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task LauncherUpdateThatRestartsStaysBusy()
    {
        //Arrange
        var packages = new FakePackageUpdater { Updates = FakePackageUpdater.LauncherPending, Restart = true };
        var session = CreateSession(packages);
        await session.CheckAsync();

        //Act
        var result = await session.UpdateLauncherAsync();

        //Assert
        Assert.Equal(UpdateResult.Restarting, result);
        Assert.True(session.IsBusy);
    }

    [Fact]
    public async Task ReplacedSessionDoesNothingAndGoesQuiet()
    {
        //Arrange
        _server.ListStatus = HttpStatusCode.InternalServerError;
        var session = CreateSession();
        var announced = 0;
        session.StateChanged += () => announced++;
        session.ErrorOccurred += _ => announced++;

        //Act
        session.Dispose();
        var result = await session.CheckAsync();

        //Assert
        Assert.Null(result);
        Assert.Equal(0, announced);
    }

    [Fact]
    public void ClientInstalledFollowsTheClient()
    {
        //Arrange
        var session = CreateSession();

        //Act
        var before = session.ClientInstalled;
        _client.IsInstalled = true;

        //Assert
        Assert.False(before);
        Assert.True(session.ClientInstalled);
    }

    private class FakeGameClient : IGameClient
    {
        public bool IsInstalled { get; set; }
        public void InstallFromZip(string zipPath) => IsInstalled = true;
        public void EnsureProfiles() { }
        public void Start() { }
    }
}
