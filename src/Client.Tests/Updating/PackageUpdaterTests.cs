using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
using FileUpdaterClient.Tests.Fakes;
using FileUpdaterClient.Updating;
using FileUpdaterPackages;

namespace FileUpdaterClient.Tests.Updating;

//Runs against an in-memory file system and fake server, so nothing touches the disk or network
public class PackageUpdaterTests : IDisposable
{
    private const string Url = "https://updates.test/";
    private const string Rid = "win-x64";
    private static readonly string Downloads = MockUnixSupport.Path(@"C:\downloads");

    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeServer _server = new();
    private readonly ECDsa _key = ManifestSigning.GenerateKey();
    private readonly FakeTazUOInstaller _tazUO;
    private readonly FakePackageState _state = new();
    private readonly List<UpdateProgress> _progress = new();
    private readonly List<UpdateErrorInfo> _errors = new();

    public PackageUpdaterTests()
    {
        _tazUO = new FakeTazUOInstaller { ReadZip = path => _fileSystem.File.ReadAllText(path) };
    }

    public void Dispose() => _key.Dispose();

    private PackageUpdater CreateUpdater(bool withTazUO = true)
    {
        var localFiles = new LocalFiles(_fileSystem);
        var server = new FileServerClient(Url, localFiles, _server);
        var downloader = new PackageDownloader(server, localFiles, Downloads) { RetryDelay = _ => TimeSpan.Zero };
        return new PackageUpdater(server, [ManifestSigning.ExportPublicKey(_key)], Rid, downloader, withTazUO ? _tazUO : null, _state);
    }

    private Task Apply(PackageUpdater updater, PackageUpdates updates) =>
        updater.ApplyAsync(updates, _progress.Add, _errors.Add, CancellationToken.None);

    [Fact]
    public async Task CheckReportsNoManifestWhenTheServerHostsNoPackages()
    {
        //Arrange
        var updater = CreateUpdater();

        //Act
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Assert
        Assert.True(updates.ManifestMissing);
        Assert.False(updates.Any);
    }

    [Fact]
    public async Task CheckRejectsAManifestSignedByAnotherKey()
    {
        //Arrange
        using var attacker = ManifestSigning.GenerateKey();
        _server.PublishPackages(attacker, (PackageRole.TazUO, "1.0.0", Rid, "evil"));
        var updater = CreateUpdater();

        //Act
        var check = () => updater.CheckAsync(CancellationToken.None);

        //Assert
        var e = await Assert.ThrowsAsync<UpdateServerException>(check);
        Assert.Equal(UpdateError.PackagesUntrusted, e.Error);
    }

    [Fact]
    public async Task CheckRejectsAManifestChangedAfterSigning()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.TazUO, "1.0.0", Rid, "tazuo"));
        _server.Manifest = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(_server.Manifest!).Replace("1.0.0", "9.9.9"));
        var updater = CreateUpdater();

        //Act
        var check = () => updater.CheckAsync(CancellationToken.None);

        //Assert
        var e = await Assert.ThrowsAsync<UpdateServerException>(check);
        Assert.Equal(UpdateError.PackagesUntrusted, e.Error);
    }

    [Fact]
    public async Task CheckRejectsAManifestWithoutASignature()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.TazUO, "1.0.0", Rid, "tazuo"));
        _server.Signature = null;
        var updater = CreateUpdater();

        //Act
        var check = () => updater.CheckAsync(CancellationToken.None);

        //Assert
        var e = await Assert.ThrowsAsync<UpdateServerException>(check);
        Assert.Equal(UpdateError.PackagesUntrusted, e.Error);
    }

    [Fact]
    public async Task CheckStillRejectsAnUntrustedManifestWhenTheTazUOLauncherIsOff()
    {
        //Arrange
        using var attacker = ManifestSigning.GenerateKey();
        _server.PublishPackages(attacker, (PackageRole.Launcher, "9.0.0", Rid, "evil"));
        var updater = CreateUpdater(withTazUO: false);

        //Act
        var check = () => updater.CheckAsync(CancellationToken.None);

        //Assert
        var e = await Assert.ThrowsAsync<UpdateServerException>(check);
        Assert.Equal(UpdateError.PackagesUntrusted, e.Error);
    }

    [Fact]
    public async Task CheckIgnoresPackagesForOtherPlatforms()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.TazUO, "9.0.0", "osx-arm64", "tazuo"));
        var updater = CreateUpdater();

        //Act
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Assert
        Assert.False(updates.Any);
        Assert.False(updates.ManifestMissing);
    }

    [Fact]
    public async Task CheckLeavesANewerLauncherToTheLauncherUpdater()
    {
        //Arrange
        _tazUO.IsInstalled = true;
        _state.TazUOVersion = new Version(2, 0, 0);
        _server.PublishPackages(_key, (PackageRole.Launcher, "9.0.0", Rid, "launcher"), (PackageRole.TazUO, "2.0.0", Rid, "tazuo"));
        var updater = CreateUpdater();

        //Act
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Assert
        Assert.False(updates.Any); //Nothing the next download has to do
    }

    [Theory]
    [InlineData(false, null, true)] //Not installed
    [InlineData(true, null, true)] //Installed before versions were recorded
    [InlineData(true, "1.0.0", true)] //Older
    [InlineData(true, "2.0.0", false)] //Current
    [InlineData(true, "2.0", false)] //Same version written differently
    [InlineData(true, "3.0.0", false)] //Newer than the server, left alone
    public async Task CheckOffersTazUOWhenMissingOrOlder(bool installed, string? recorded, bool offered)
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.TazUO, "2.0.0", Rid, "tazuo"));
        _tazUO.IsInstalled = installed;
        _state.TazUOVersion = recorded == null ? null : Version.Parse(recorded);
        var updater = CreateUpdater();

        //Act
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Assert
        Assert.Equal(offered, updates.TazUO != null);
    }

    [Fact]
    public async Task CheckOffersNothingWhenTheTazUOLauncherIsOff()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.TazUO, "2.0.0", Rid, "tazuo"));
        var updater = CreateUpdater(withTazUO: false);

        //Act
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Assert
        Assert.Null(updates.TazUO);
    }

    [Fact]
    public async Task ApplyInstallsTheVerifiedPackageAndRemembersItsVersion()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.TazUO, "2.0.0", Rid, "tazuo-bytes"));
        var updater = CreateUpdater();
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        await Apply(updater, updates);

        //Assert
        Assert.Equal("tazuo-bytes", _tazUO.InstalledContent);
        Assert.Equal(new Version(2, 0, 0), _state.TazUOVersion);
        Assert.Equal(1, _tazUO.ProfileSetups);
        Assert.False(_fileSystem.File.Exists(_tazUO.InstalledFrom!)); //The downloaded zip is cleaned up
        Assert.Empty(_errors);
        Assert.Equal(UpdatePhase.InstallingTazUO, _progress.Last().Phase);
        Assert.Equal(1, _progress.Last().Done);
    }

    [Fact]
    public async Task ApplyRejectsAPackageThatDoesntMatchTheSignedHash()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.TazUO, "2.0.0", Rid, "genuine"));
        _server.TamperWithPackage(PackageRole.TazUO, "2.0.0", Rid, "malicious");
        var updater = CreateUpdater();
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        await Apply(updater, updates);

        //Assert
        Assert.False(_tazUO.IsInstalled);
        Assert.Null(_tazUO.InstalledFrom);
        Assert.Null(_state.TazUOVersion);
        Assert.Equal([new UpdateErrorInfo(UpdateError.TazUOInstallFailed)], _errors);
        Assert.Equal(3, _server.PackageDownloads.Count); //Tried three times
        Assert.DoesNotContain(_fileSystem.AllFiles, f => f.StartsWith(Downloads)); //Nothing left behind
    }

    [Fact]
    public async Task ApplyReportsAFailedInstallAndDoesntRecordTheVersion()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.TazUO, "2.0.0", Rid, "tazuo"));
        _tazUO.InstallError = new IOException("locked");
        var updater = CreateUpdater();
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        await Apply(updater, updates);

        //Assert
        Assert.Null(_state.TazUOVersion);
        Assert.Equal([new UpdateErrorInfo(UpdateError.TazUOInstallFailed)], _errors);
    }

    [Theory]
    [InlineData("..\\evil.zip")]
    [InlineData("../evil.zip")]
    [InlineData("sub/tazuo-2.0.0.win-x64.zip")]
    public async Task ApplyNeverDownloadsAPackageWhoseNameIsNotAPlainFileName(string file)
    {
        //Arrange
        var updater = CreateUpdater();
        var package = new PackageEntry(PackageRole.TazUO, "2.0.0", Rid, file, "abc", 1);

        //Act
        await Apply(updater, new PackageUpdates(package, ManifestMissing: false));

        //Assert
        Assert.Empty(_server.PackageDownloads);
        Assert.False(_tazUO.IsInstalled);
        Assert.False(_fileSystem.Directory.Exists(Downloads)); //Nothing was created, not even the download folder
        Assert.Equal([new UpdateErrorInfo(UpdateError.TazUOInstallFailed)], _errors);
    }

    [Fact]
    public async Task ApplyReportsAMissingTazUOLauncherWhenTheServerHasNothingToInstallItFrom()
    {
        //Arrange
        var updater = CreateUpdater(); //No manifest at all
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        await Apply(updater, updates);

        //Assert
        Assert.False(_tazUO.IsInstalled);
        Assert.Equal(0, _tazUO.ProfileSetups);
        Assert.Equal([new UpdateErrorInfo(UpdateError.TazUOInstallFailed)], _errors);
    }

    [Fact]
    public async Task ApplyKeepsAnInstalledTazUOLaunchersProfilesUpToDateWhenNothingIsPending()
    {
        //Arrange
        _tazUO.IsInstalled = true;
        var updater = CreateUpdater();

        //Act
        await Apply(updater, PackageUpdates.None);

        //Assert
        Assert.Equal(1, _tazUO.ProfileSetups);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task ApplyDoesNothingWhenTheTazUOLauncherIsOff()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.TazUO, "2.0.0", Rid, "tazuo"));
        var updater = CreateUpdater(withTazUO: false);
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        await Apply(updater, updates);

        //Assert
        Assert.Empty(_server.PackageDownloads);
        Assert.Empty(_errors);
        Assert.Equal(0, _tazUO.ProfileSetups);
    }
}
