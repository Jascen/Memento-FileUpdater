using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Security.Cryptography;
using FileUpdaterClient.Config;
using FileUpdaterClient.Tests.Fakes;
using FileUpdaterClient.Updating;
using FileUpdaterClient.ViewModels;
using FileUpdaterPackages;

namespace FileUpdaterClient.Tests.ViewModels;

//The view model over real update services, which in turn run against an in-memory file system and fake server
public class MainViewModelTests : IDisposable
{
    private const string Url = "https://updates.test/";
    private const string Rid = "win-x64";
    private static readonly string InstallPath = MockUnixSupport.Path(@"C:\game");
    private static readonly string OtherPath = MockUnixSupport.Path(@"C:\other");
    private static readonly string Downloads = MockUnixSupport.Path(@"C:\downloads");

    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeServer _server = new();
    private readonly ECDsa _key = ManifestSigning.GenerateKey();
    private readonly FakeSettings _settings = new() { InstallPath = InstallPath };
    private readonly FakeMainView _view = new();
    private readonly FakeGameLauncher _tazUO = new();
    private readonly FakeSelfUpdater _self = new();
    private readonly List<string> _openedFolders = new();

    public MainViewModelTests()
    {
        _fileSystem.Directory.CreateDirectory(InstallPath);
        _fileSystem.Directory.CreateDirectory(OtherPath);
    }

    public void Dispose() => _key.Dispose();

    private MainViewModel CreateViewModel(bool withLauncherUpdater = false)
    {
        var localFiles = new LocalFiles(_fileSystem);
        var server = new FileServerClient(Url, localFiles, _server);
        var launcherUpdater = !withLauncherUpdater ? null
            : new LauncherUpdater(server, [ManifestSigning.ExportPublicKey(_key)], Rid, new Version(1, 0, 0),
                new PackageDownloader(server, localFiles, Downloads) { RetryDelay = _ => TimeSpan.Zero }, _self);

        InstallSession OpenFolder(string installPath)
        {
            _openedFolders.Add(installPath);
            var updates = new UpdateService(server, localFiles, installPath, packages: null) { RetryDelay = _ => TimeSpan.Zero };
            return new InstallSession(updates, _tazUO);
        }

        return new MainViewModel(_settings, OpenFolder, launcherUpdater, new ImmediateUiThread());
    }

    private void WriteLocal(string folder, string name, string content) =>
        _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(folder, name), content);

    [Fact]
    public async Task StartVerifiesTheFilesAndOffersPlay()
    {
        //Arrange
        _server.Add("a.mul", "same");
        WriteLocal(InstallPath, "a.mul", "same");
        var viewModel = CreateViewModel();

        //Act
        await viewModel.StartAsync(_view);

        //Assert
        Assert.Equal(LauncherState.Verified, viewModel.State);
        Assert.True(viewModel.FilesVerified);
        Assert.True(viewModel.CanPlay);
        Assert.Equal(Strings.PlayText, viewModel.MainButtonText);
        Assert.Equal(Strings.Finished, viewModel.ProgressText);
        Assert.Equal(100, viewModel.Progress);
    }

    [Fact]
    public async Task StartOffersTheDownloadWhenFilesDiffer()
    {
        //Arrange
        _server.Add("a.mul", "new");
        WriteLocal(InstallPath, "a.mul", "old");
        var viewModel = CreateViewModel();

        //Act
        await viewModel.StartAsync(_view);

        //Assert
        Assert.Equal(LauncherState.UpdatesReady, viewModel.State);
        Assert.True(viewModel.DownloadsReady);
        Assert.False(viewModel.CanPlay);
        Assert.True(viewModel.MainButtonEnabled);
        Assert.Equal(Strings.DownloadButton, viewModel.MainButtonText);
        Assert.Empty(_server.Downloads);
    }

    [Fact]
    public async Task TheMainButtonDownloadsPendingUpdatesThenBecomesPlay()
    {
        //Arrange
        _server.Add("a.mul", "new");
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(_view);

        //Act
        await viewModel.MainButtonAsync();

        //Assert
        Assert.Equal(LauncherState.Verified, viewModel.State);
        Assert.Equal(Strings.PlayText, viewModel.MainButtonText);
        Assert.Equal("new", _fileSystem.File.ReadAllText(_fileSystem.Path.Combine(InstallPath, "a.mul")));
        Assert.Equal(0, _tazUO.Starts); //That click downloaded, it didn't also play
    }

    [Fact]
    public async Task AFailedCheckShowsTheErrorAndOffersRetry()
    {
        //Arrange
        _server.ListStatus = HttpStatusCode.ServiceUnavailable;
        var viewModel = CreateViewModel();

        //Act
        await viewModel.StartAsync(_view);

        //Assert
        Assert.Equal(LauncherState.Failed, viewModel.State);
        Assert.True(viewModel.RetryReady);
        Assert.False(viewModel.IsUpdating);
        Assert.Equal(Strings.ConError, viewModel.ErrorMessage);
        Assert.Equal(Strings.CheckFailed, viewModel.ProgressText);
    }

    [Fact]
    public async Task RetryChecksAgainAndDownloadsStraightAway()
    {
        //Arrange
        _server.Add("a.mul", "new");
        _server.ListStatus = HttpStatusCode.ServiceUnavailable;
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(_view);
        _server.ListStatus = null;

        //Act
        await viewModel.RetryAsync();

        //Assert
        Assert.Equal(LauncherState.Verified, viewModel.State);
        Assert.Equal(["a.mul"], _server.Downloads);
    }

    [Fact]
    public async Task FilesThatCouldntBeDownloadedEndInFailedNotVerified()
    {
        //Arrange
        _server.Add("a.mul", "new");
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(_view);
        _server.FailuresBeforeSuccess = 5;

        //Act
        await viewModel.MainButtonAsync();

        //Assert
        Assert.Equal(LauncherState.Failed, viewModel.State);
        Assert.False(viewModel.FilesVerified);
        Assert.Equal(string.Format(Strings.FinishedWithFailures, 1), viewModel.ProgressText);
        Assert.Equal(string.Format(Strings.FileFailedError, "a.mul"), viewModel.ErrorMessage);
    }

    [Fact]
    public async Task NothingIsCheckedWhenVerifyOnLaunchIsOff()
    {
        //Arrange
        _settings.Preferences.VerifyOnLaunch = false;
        var viewModel = CreateViewModel();

        //Act
        await viewModel.StartAsync(_view);

        //Assert
        Assert.Equal(LauncherState.Idle, viewModel.State);
        Assert.Equal(Strings.NotVerified, viewModel.ProgressText);
        Assert.Equal(0, _server.Requests);
        Assert.True(viewModel.CanPlay);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task PlayingWithUnverifiedFilesAsksFirst(bool playAnyway, int starts)
    {
        //Arrange
        _settings.Preferences.VerifyOnLaunch = false;
        _view.ConfirmAnswer = playAnyway;
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(_view);

        //Act
        await viewModel.MainButtonAsync();

        //Assert
        Assert.Equal([Strings.UnverifiedTitle], _view.Confirmations);
        Assert.Equal(starts, _tazUO.Starts);
    }

    [Fact]
    public async Task PlayingWithVerifiedFilesDoesntAsk()
    {
        //Arrange
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(_view);

        //Act
        await viewModel.MainButtonAsync();

        //Assert
        Assert.Empty(_view.Confirmations);
        Assert.Equal(1, _tazUO.Starts);
    }

    [Fact]
    public async Task AnUnusableFolderOpensSettingsAndWaitsForAnotherOne()
    {
        //Arrange
        _settings.Usable = false;
        var viewModel = CreateViewModel();

        //Act
        await viewModel.StartAsync(_view);

        //Assert
        Assert.Equal(1, _view.SettingsShown);
        Assert.Equal(LauncherState.Idle, viewModel.State);
        Assert.Equal(Strings.NoFolderChosen, viewModel.ProgressText);
        Assert.Empty(_openedFolders);
        Assert.Equal(0, _server.Requests);
    }

    [Fact]
    public async Task PickingAnotherFolderStartsOverThere()
    {
        //Arrange
        _server.Add("a.mul", "same");
        WriteLocal(InstallPath, "a.mul", "same");
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(_view);
        _view.FolderToPick = OtherPath;

        //Act
        await viewModel.OpenSettingsAsync();

        //Assert
        Assert.Equal([InstallPath, OtherPath], _openedFolders);
        Assert.Equal(LauncherState.UpdatesReady, viewModel.State); //The new folder doesn't have a.mul yet
    }

    [Fact]
    public async Task ANewerLauncherIsOfferedWithoutHoldingAnythingUp()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        var viewModel = CreateViewModel(withLauncherUpdater: true);

        //Act
        await viewModel.StartAsync(_view);

        //Assert
        Assert.True(viewModel.LauncherUpdateAvailable);
        Assert.Equal(string.Format(Strings.LauncherUpdateAvailable, "1.5.0"), viewModel.LauncherUpdateText);
        Assert.Equal(LauncherState.Verified, viewModel.State);
        Assert.True(viewModel.CanPlay);
    }

    [Fact]
    public async Task ADismissedLauncherUpdateStaysHidden()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        var viewModel = CreateViewModel(withLauncherUpdater: true);
        await viewModel.StartAsync(_view);

        //Act
        viewModel.DismissLauncherUpdate();
        await viewModel.RefreshLauncherUpdateAsync();

        //Assert
        Assert.False(viewModel.LauncherUpdateAvailable);
    }

    [Fact]
    public async Task UpdatingTheLauncherClosesTheWindowForTheRestart()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        _self.Restarts = true;
        var viewModel = CreateViewModel(withLauncherUpdater: true);
        await viewModel.StartAsync(_view);

        //Act
        await viewModel.UpdateLauncherAsync();

        //Assert
        Assert.True(_view.ShutDown);
        Assert.Equal(Strings.Restarting, viewModel.ProgressText);
        Assert.True(viewModel.IsUpdating); //Stays busy until the process exits
    }

    [Fact]
    public async Task ALauncherUpdateThatIsNotAppliedPutsThingsBackAsTheyWere()
    {
        //Arrange
        _server.Add("a.mul", "new");
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        var viewModel = CreateViewModel(withLauncherUpdater: true); //_self.Restarts is false
        await viewModel.StartAsync(_view);

        //Act
        await viewModel.UpdateLauncherAsync();

        //Assert
        Assert.False(_view.ShutDown);
        Assert.Equal(LauncherState.UpdatesReady, viewModel.State); //The file download is still waiting
        Assert.Equal(Strings.UpdatesReady, viewModel.ProgressText);
        Assert.False(viewModel.LauncherUpdateAvailable); //Not offered again this run
    }

    [Fact]
    public async Task ADeclinedLauncherUpdateIsNotOfferedAgainAfterChangingFolders()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        var viewModel = CreateViewModel(withLauncherUpdater: true); //_self.Restarts is false
        await viewModel.StartAsync(_view);
        await viewModel.UpdateLauncherAsync();
        _view.FolderToPick = OtherPath;
        await viewModel.OpenSettingsAsync();

        //Act
        await viewModel.RefreshLauncherUpdateAsync();

        //Assert
        Assert.Equal([InstallPath, OtherPath], _openedFolders);
        Assert.False(viewModel.LauncherUpdateAvailable);
    }
}
