using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
using FileUpdaterClient.Tests.Fakes;
using FileUpdaterClient.Updating;
using FileUpdaterPackages;

namespace FileUpdaterClient.Tests.Updating;

//Runs against an in-memory file system and fake server, so nothing touches the disk or network
public class LauncherUpdaterTests : IDisposable
{
    private const string Url = "https://updates.test/";
    private const string Rid = "win-x64";
    private static readonly string Downloads = MockUnixSupport.Path(@"C:\downloads");

    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeServer _server = new();
    private readonly ECDsa _key = ManifestSigning.GenerateKey();
    private readonly FakeSelfUpdater _self;
    private readonly List<UpdateProgress> _progress = new();
    private readonly List<UpdateErrorInfo> _errors = new();

    public LauncherUpdaterTests()
    {
        _self = new FakeSelfUpdater { ReadZip = path => _fileSystem.File.ReadAllText(path) };
    }

    public void Dispose() => _key.Dispose();

    private LauncherUpdater CreateUpdater(string running = "1.0.0")
    {
        var localFiles = new LocalFiles(_fileSystem);
        var server = new FileServerClient(Url, localFiles, _server);
        var downloader = new PackageDownloader(server, localFiles, Downloads) { RetryDelay = _ => TimeSpan.Zero };
        var updater = new LauncherUpdater(server, [ManifestSigning.ExportPublicKey(_key)], Rid, Version.Parse(running), downloader, _self);
        updater.ProgressChanged += _progress.Add;
        updater.ErrorOccurred += _errors.Add;
        return updater;
    }

    [Theory]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("1.2", "1.2.1", true)]
    [InlineData("1.2.0", "1.2", false)] //Same version written differently
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("2.0.0", "1.9.0", false)] //Never offers a downgrade
    public async Task RefreshOffersALauncherOnlyWhenNewer(string running, string hosted, bool offered)
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, hosted, Rid, "launcher"));
        var updater = CreateUpdater(running);

        //Act
        await updater.RefreshAsync();

        //Assert
        Assert.Equal(offered, updater.Available != null);
    }

    [Fact]
    public async Task RefreshIgnoresLaunchersForOtherPlatforms()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "9.0.0", "linux-x64", "launcher"));
        var updater = CreateUpdater();

        //Act
        await updater.RefreshAsync();

        //Assert
        Assert.Null(updater.Available);
    }

    [Fact]
    public async Task RefreshOffersNothingFromAManifestSignedByAnotherKey()
    {
        //Arrange
        using var attacker = ManifestSigning.GenerateKey();
        _server.PublishPackages(attacker, (PackageRole.Launcher, "9.0.0", Rid, "evil"));
        var updater = CreateUpdater();

        //Act
        await updater.RefreshAsync();

        //Assert
        Assert.Null(updater.Available);
    }

    [Fact]
    public async Task AFailedRefreshKeepsWhatWasKnown()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        var updater = CreateUpdater();
        await updater.RefreshAsync();
        _server.Signature = null; //The manifest can't be trusted any more

        //Act
        await updater.RefreshAsync();

        //Assert
        Assert.Equal("1.5.0", updater.Available?.Version);
    }

    [Fact]
    public async Task UpdatePassesTheVerifiedPackageToTheSelfUpdater()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher-bytes"), (PackageRole.TazUO, "2.0.0", Rid, "tazuo"));
        _self.Restarts = true;
        var updater = CreateUpdater();
        await updater.RefreshAsync();

        //Act
        var result = await updater.UpdateAsync();

        //Assert
        Assert.Equal(UpdateResult.Restarting, result);
        Assert.Equal(new Version(1, 5, 0), Assert.Single(_self.Applied).Version);
        Assert.Equal("launcher-bytes", _self.AppliedContent);
        Assert.Equal(["launcher-1.5.0.win-x64.zip"], _server.PackageDownloads); //The TazUO package isn't its business
        Assert.Equal(UpdatePhase.UpdatingLauncher, _progress.Last().Phase);
    }

    [Fact]
    public async Task UpdateDoesNothingWhenThereIsNoNewerLauncher()
    {
        //Arrange
        var updater = CreateUpdater();
        await updater.RefreshAsync();

        //Act
        var result = await updater.UpdateAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.Empty(_self.Applied);
        Assert.Empty(_server.PackageDownloads);
    }

    [Fact]
    public async Task UpdateCarriesOnWhenTheSelfUpdaterDeclines()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        var updater = CreateUpdater(); //_self.Restarts is false
        await updater.RefreshAsync();

        //Act
        var result = await updater.UpdateAsync();
        var afterUpdate = updater.Available;
        await updater.RefreshAsync();

        //Assert
        Assert.Equal(UpdateResult.Failed, result);
        Assert.Empty(_errors);
        Assert.Null(afterUpdate);
        Assert.Null(updater.Available); //Not offered again this run
        Assert.DoesNotContain(_fileSystem.AllFiles, f => f.StartsWith(Downloads));
    }

    [Fact]
    public async Task UpdateReportsASelfUpdateFailure()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        _self.Error = new InvalidOperationException("swap failed");
        var updater = CreateUpdater();
        await updater.RefreshAsync();

        //Act
        var result = await updater.UpdateAsync();

        //Assert
        Assert.Equal(UpdateResult.Failed, result);
        Assert.Equal([new UpdateErrorInfo(UpdateError.SelfUpdateFailed)], _errors);
    }

    [Fact]
    public async Task UpdateReportsAPackageThatCantBeVerifiedAndCanBeTriedAgain()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        _server.TamperWithPackage(PackageRole.Launcher, "1.5.0", Rid, "not what was signed");
        var updater = CreateUpdater();
        await updater.RefreshAsync();

        //Act
        var result = await updater.UpdateAsync();
        await updater.RefreshAsync();

        //Assert
        Assert.Equal(UpdateResult.Failed, result);
        Assert.Empty(_self.Applied); //Never handed an unverified package
        Assert.Equal([new UpdateErrorInfo(UpdateError.SelfUpdateFailed)], _errors);
        Assert.NotNull(updater.Available); //Still offered, the download may just have been corrupted
    }

    [Fact]
    public async Task ACancelledUpdateReturnsCancelledAndCanBeRunAgain()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        _self.Restarts = true;
        var updater = CreateUpdater();
        await updater.RefreshAsync();
        void CancelOnce(UpdateProgress _)
        {
            updater.ProgressChanged -= CancelOnce;
            updater.Cancel();
        }
        updater.ProgressChanged += CancelOnce;

        //Act
        var cancelled = await updater.UpdateAsync();
        var again = await updater.UpdateAsync();

        //Assert
        Assert.Equal(UpdateResult.Cancelled, cancelled);
        Assert.Equal(UpdateResult.Restarting, again);
    }
}
