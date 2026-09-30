using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
using FileUpdaterClient.Tests.Fakes;
using FileUpdaterClient.Updating;
using FileUpdaterPackages;

namespace FileUpdaterClient.Tests.Updating;

//Runs against an in-memory file system and fake server, so nothing touches the disk or network
public class PackageUpdaterTests : IDisposable
{
    private const string Url = "http://updates.test/";
    private const string Rid = "win-x64";
    private static readonly string InstallPath = MockUnixSupport.Path(@"C:\game");
    private static readonly string Downloads = MockUnixSupport.Path(@"C:\downloads");

    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeServer _server = new();
    private readonly ECDsa _key = ManifestSigning.GenerateKey();
    private readonly FakeClientInstaller _client;
    private readonly FakeSelfUpdater _self;
    private readonly FakePackageState _state = new();
    private readonly List<UpdateProgress> _progress = new();
    private readonly List<UpdateErrorInfo> _errors = new();

    public PackageUpdaterTests()
    {
        _client = new FakeClientInstaller { ReadZip = path => _fileSystem.File.ReadAllText(path) };
        _self = new FakeSelfUpdater { ReadZip = path => _fileSystem.File.ReadAllText(path) };
    }

    public void Dispose() => _key.Dispose();

    private PackageUpdater CreateUpdater(string launcherVersion = "1.0.0", bool withClient = true, ECDsa? trustedKey = null)
    {
        var localFiles = new LocalFiles(_fileSystem);
        return new PackageUpdater(new FileServerClient(Url, localFiles, _server), localFiles,
            [ManifestSigning.ExportPublicKey(trustedKey ?? _key)], Rid, Version.Parse(launcherVersion), Downloads, _self,
            withClient ? _client : null, _state)
        {
            RetryDelay = _ => TimeSpan.Zero,
        };
    }

    private Task Apply(PackageUpdater updater, PackageUpdates updates) =>
        updater.ApplyAsync(updates, _progress.Add, _errors.Add, CancellationToken.None);

    private Task<bool> UpdateLauncher(PackageUpdater updater, PackageUpdates updates) =>
        updater.UpdateLauncherAsync(updates.Launcher!, _progress.Add, _errors.Add, CancellationToken.None);

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
        _server.PublishPackages(attacker, (PackageRole.Client, "1.0.0", Rid, "evil"));
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
        _server.PublishPackages(_key, (PackageRole.Client, "1.0.0", Rid, "client"));
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
        _server.PublishPackages(_key, (PackageRole.Client, "1.0.0", Rid, "client"));
        _server.Signature = null;
        var updater = CreateUpdater();

        //Act
        var check = () => updater.CheckAsync(CancellationToken.None);

        //Assert
        var e = await Assert.ThrowsAsync<UpdateServerException>(check);
        Assert.Equal(UpdateError.PackagesUntrusted, e.Error);
    }

    [Theory]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("1.2", "1.2.1", true)]
    [InlineData("1.2.0", "1.2", false)] //Same version written differently
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("2.0.0", "1.9.0", false)] //Never offers a downgrade
    public async Task CheckOffersALauncherOnlyWhenNewer(string running, string hosted, bool offered)
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, hosted, Rid, "launcher"));
        var updater = CreateUpdater(running);

        //Act
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Assert
        Assert.Equal(offered, updates.Launcher != null);
    }

    [Fact]
    public async Task CheckIgnoresPackagesForOtherPlatforms()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "9.0.0", "linux-x64", "launcher"), (PackageRole.Client, "9.0.0", "osx-arm64", "client"));
        var updater = CreateUpdater();

        //Act
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Assert
        Assert.False(updates.Any);
        Assert.False(updates.ManifestMissing);
    }

    [Theory]
    [InlineData(false, null, true)] //Not installed
    [InlineData(true, null, true)] //Installed before versions were recorded
    [InlineData(true, "1.0.0", true)] //Older
    [InlineData(true, "2.0.0", false)] //Current
    [InlineData(true, "3.0.0", false)] //Newer than the server, left alone
    public async Task CheckOffersTheClientWhenMissingOrOlder(bool installed, string? recorded, bool offered)
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Client, "2.0.0", Rid, "client"));
        _client.IsInstalled = installed;
        _state.ClientVersion = recorded == null ? null : Version.Parse(recorded);
        var updater = CreateUpdater();

        //Act
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Assert
        Assert.Equal(offered, updates.Client != null);
    }

    [Fact]
    public async Task CheckOffersNoClientWhenTheTazUOLauncherIsOff()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Client, "2.0.0", Rid, "client"));
        var updater = CreateUpdater(withClient: false);

        //Act
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Assert
        Assert.Null(updates.Client);
    }

    [Fact]
    public async Task ApplyInstallsTheVerifiedClientAndRemembersItsVersion()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Client, "2.0.0", Rid, "client-bytes"));
        var updater = CreateUpdater();
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        await Apply(updater, updates);

        //Assert
        Assert.Equal("client-bytes", _client.InstalledContent);
        Assert.Equal(new Version(2, 0, 0), _state.ClientVersion);
        Assert.Equal(1, _client.ProfileSetups);
        Assert.False(_fileSystem.File.Exists(_client.InstalledFrom!)); //The downloaded zip is cleaned up
        Assert.Empty(_errors);
        Assert.Equal(UpdatePhase.InstallingClient, _progress.Last().Phase);
        Assert.Equal(1, _progress.Last().Done);
    }

    [Fact]
    public async Task ApplyRejectsAPackageThatDoesntMatchTheSignedHash()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Client, "2.0.0", Rid, "genuine"));
        _server.TamperWithPackage(PackageRole.Client, "2.0.0", Rid, "malicious");
        var updater = CreateUpdater();
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        await Apply(updater, updates);

        //Assert
        Assert.False(_client.IsInstalled);
        Assert.Null(_client.InstalledFrom);
        Assert.Null(_state.ClientVersion);
        Assert.Equal([new UpdateErrorInfo(UpdateError.LauncherFailed)], _errors);
        Assert.Equal(3, _server.PackageDownloads.Count); //Tried three times
        Assert.DoesNotContain(_fileSystem.AllFiles, f => f.StartsWith(Downloads)); //Nothing left behind
    }

    [Fact]
    public async Task ApplyReportsAFailedInstallAndDoesntRecordTheVersion()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Client, "2.0.0", Rid, "client"));
        _client.InstallError = new IOException("locked");
        var updater = CreateUpdater();
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        await Apply(updater, updates);

        //Assert
        Assert.Null(_state.ClientVersion);
        Assert.Equal([new UpdateErrorInfo(UpdateError.LauncherFailed)], _errors);
    }

    [Fact]
    public async Task UpdateLauncherPassesTheVerifiedPackageToTheSelfUpdater()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher-bytes"), (PackageRole.Client, "2.0.0", Rid, "client"));
        _self.Restarts = true;
        var updater = CreateUpdater();
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        var restarting = await UpdateLauncher(updater, updates);

        //Assert
        Assert.True(restarting);
        Assert.Equal(new Version(1, 5, 0), Assert.Single(_self.Applied).Version);
        Assert.Equal("launcher-bytes", _self.AppliedContent);
        Assert.False(_client.IsInstalled); //The new launcher installs the client
        Assert.Equal(UpdatePhase.UpdatingLauncher, _progress.Last().Phase);
    }

    [Fact]
    public async Task ApplyLeavesTheLauncherAloneEvenWhenANewerOneIsHosted()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"), (PackageRole.Client, "2.0.0", Rid, "client"));
        var updater = CreateUpdater();
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        await Apply(updater, updates);

        //Assert
        Assert.Empty(_self.Applied);
        Assert.True(_client.IsInstalled);
        Assert.Empty(_errors);
        Assert.DoesNotContain(_progress, p => p.Phase == UpdatePhase.UpdatingLauncher); //Never even downloaded
    }

    [Fact]
    public async Task ALauncherUpdateAloneIsNotSomethingTheNextDownloadNeedsToDo()
    {
        //Arrange
        _client.IsInstalled = true;
        _state.ClientVersion = new Version(2, 0, 0);
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"), (PackageRole.Client, "2.0.0", Rid, "client"));
        var updater = CreateUpdater();

        //Act
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Assert
        Assert.NotNull(updates.Launcher);
        Assert.False(updates.Any);
    }

    [Fact]
    public async Task UpdateLauncherCarriesOnWhenTheSelfUpdaterDeclines()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        var updater = CreateUpdater(); //_self.Restarts is false
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        var restarting = await UpdateLauncher(updater, updates);
        var next = await updater.CheckAsync(CancellationToken.None);

        //Assert
        Assert.False(restarting);
        Assert.Empty(_errors);
        Assert.Null(next.Launcher); //Not offered again this run
        Assert.DoesNotContain(_fileSystem.AllFiles, f => f.StartsWith(Downloads));
    }

    [Fact]
    public async Task UpdateLauncherReportsASelfUpdateFailure()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        _self.Error = new InvalidOperationException("swap failed");
        var updater = CreateUpdater();
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        var restarting = await UpdateLauncher(updater, updates);

        //Assert
        Assert.False(restarting);
        Assert.Equal([new UpdateErrorInfo(UpdateError.SelfUpdateFailed)], _errors);
    }

    [Fact]
    public async Task UpdateLauncherReportsAPackageThatCantBeVerifiedAndCanBeTriedAgain()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        _server.TamperWithPackage(PackageRole.Launcher, "1.5.0", Rid, "not what was signed");
        var updater = CreateUpdater();
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        var restarting = await UpdateLauncher(updater, updates);
        var next = await updater.CheckAsync(CancellationToken.None);

        //Assert
        Assert.False(restarting);
        Assert.Empty(_self.Applied); //Never handed an unverified package
        Assert.Equal([new UpdateErrorInfo(UpdateError.SelfUpdateFailed)], _errors);
        Assert.NotNull(next.Launcher); //Still offered, the download may just have been corrupted
    }

    [Theory]
    [InlineData("..\\evil.zip")]
    [InlineData("../evil.zip")]
    [InlineData("sub/client-2.0.0.win-x64.zip")]
    public async Task ApplyNeverDownloadsAPackageWhoseNameIsNotAPlainFileName(string file)
    {
        //Arrange
        var updater = CreateUpdater();
        var package = new PackageEntry(PackageRole.Client, "2.0.0", Rid, file, "abc", 1);

        //Act
        await Apply(updater, new PackageUpdates(null, package, ManifestMissing: false));

        //Assert
        Assert.Empty(_server.PackageDownloads);
        Assert.False(_client.IsInstalled);
        Assert.False(_fileSystem.Directory.Exists(Downloads)); //Nothing was created, not even the download folder
        Assert.Equal([new UpdateErrorInfo(UpdateError.LauncherFailed)], _errors);
    }

    [Fact]
    public async Task ApplyReportsAMissingClientWhenTheServerHasNothingToInstallItFrom()
    {
        //Arrange
        var updater = CreateUpdater(); //No manifest at all
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        await Apply(updater, updates);

        //Assert
        Assert.False(_client.IsInstalled);
        Assert.Equal(0, _client.ProfileSetups);
        Assert.Equal([new UpdateErrorInfo(UpdateError.LauncherFailed)], _errors);
    }

    [Fact]
    public async Task ApplyKeepsAnInstalledClientsProfilesUpToDateWhenNothingIsPending()
    {
        //Arrange
        _client.IsInstalled = true;
        var updater = CreateUpdater();

        //Act
        await Apply(updater, PackageUpdates.None);

        //Assert
        Assert.Equal(1, _client.ProfileSetups);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task UpdateLauncherWorksWhenTheTazUOLauncherIsOff()
    {
        //Arrange
        _server.PublishPackages(_key, (PackageRole.Launcher, "1.5.0", Rid, "launcher"));
        var updater = CreateUpdater(withClient: false);
        var updates = await updater.CheckAsync(CancellationToken.None);

        //Act
        await UpdateLauncher(updater, updates);
        await Apply(updater, updates);

        //Assert
        Assert.Single(_self.Applied);
        Assert.Empty(_errors);
        Assert.Equal(0, _client.ProfileSetups);
    }
}
