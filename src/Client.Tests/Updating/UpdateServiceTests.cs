using System.IO.Abstractions.TestingHelpers;
using System.Net;
using FileUpdaterClient.Tests.Fakes;
using FileUpdaterClient.Updating;

namespace FileUpdaterClient.Tests.Updating;

//Runs against an in-memory file system and fake server, so nothing touches the disk or network
public class UpdateServiceTests
{
    private const string Url = "https://updates.test/";
    private static readonly string InstallPath = MockUnixSupport.Path(@"C:\game");
    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeServer _server = new();

    public UpdateServiceTests() => _fileSystem.Directory.CreateDirectory(InstallPath);

    private UpdateService CreateService(IPackageUpdater? packages = null, string[]? keepLocalFiles = null,
        string[]? reservedPaths = null, string url = Url, Func<bool>? allowInsecure = null)
    {
        var localFiles = new LocalFiles(_fileSystem);
        return new UpdateService(new FileServerClient(url, localFiles, _server, allowInsecure), localFiles, InstallPath, packages, keepLocalFiles, reservedPaths)
        {
            RetryDelay = _ => TimeSpan.Zero,
        };
    }

    private void WriteLocal(string name, string content)
    {
        var path = _fileSystem.Path.Combine(InstallPath, name);
        _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(path)!);
        _fileSystem.File.WriteAllText(path, content);
    }

    private string ReadLocal(string name) => _fileSystem.File.ReadAllText(_fileSystem.Path.Combine(InstallPath, name));

    [Fact]
    public async Task CheckFinishesWhenFilesMatch()
    {
        //Arrange
        _server.Add("a.mul", "same");
        WriteLocal("a.mul", "same");
        var service = CreateService();

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.True(service.FilesVerified);
        Assert.Empty(_server.Downloads);
    }

    [Fact]
    public async Task CheckReportsUpdatesWithoutDownloading()
    {
        //Arrange
        _server.Add("a.mul", "new");
        WriteLocal("a.mul", "old");
        var service = CreateService();

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.UpdatesReady, result);
        Assert.False(service.FilesVerified);
        Assert.Empty(_server.Downloads);
    }

    [Fact]
    public async Task DownloadReplacesChangedAndMissingFiles()
    {
        //Arrange
        _server.Add("a.mul", "new");
        _server.Add("maps/b.mul", "missing");
        WriteLocal("a.mul", "old");
        var service = CreateService();
        await service.CheckAsync();

        //Act
        var result = await service.DownloadAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.True(service.FilesVerified);
        Assert.Equal("new", ReadLocal("a.mul"));
        Assert.Equal("missing", ReadLocal(Path.Combine("maps", "b.mul")));
    }

    [Fact]
    public async Task DownloadRetriesFailedFiles()
    {
        //Arrange
        _server.Add("a.mul", "new");
        _server.FailuresBeforeSuccess = 1;
        var service = CreateService();
        await service.CheckAsync();

        //Act
        var result = await service.DownloadAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.Equal(2, _server.Downloads.Count);
        Assert.True(service.FilesVerified);
    }

    [Fact]
    public async Task ServerErrorFailsTheCheck()
    {
        //Arrange
        _server.ListStatus = HttpStatusCode.InternalServerError;
        var errors = new List<UpdateErrorInfo>();
        var service = CreateService();
        service.ErrorOccurred += errors.Add;

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Failed, result);
        Assert.Equal(UpdateError.ConnectionFailed, Assert.Single(errors).Error);
    }

    [Fact]
    public async Task MalformedListIsBadData()
    {
        //Arrange
        _server.RawList = "not json";
        var errors = new List<UpdateErrorInfo>();
        var service = CreateService();
        service.ErrorOccurred += errors.Add;

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Failed, result);
        Assert.Equal(UpdateError.BadData, Assert.Single(errors).Error);
    }

    [Fact]
    public async Task NamesOutsideInstallFolderAreSkipped()
    {
        //Arrange
        _server.RawList = $$"""[{"name":"../evil.txt","md5":"{{FakeServer.Md5("x")}}"}]""";
        var service = CreateService();

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.Empty(_server.Downloads);
    }

    [Theory]
    [InlineData("TazUO Launcher/TazUOLauncher.exe")]
    [InlineData("tazuo launcher/plugins/evil.dll")] //Case doesn't get around it
    [InlineData("maps/../TazUO Launcher/TazUOLauncher.exe")]
    [InlineData("TazUO Launcher")]
    [InlineData(HashCache.FileName)] //The launcher's own record of what it hashed
    public async Task ReservedPathsAreNeverDownloaded(string name)
    {
        //Arrange
        _server.RawList = $$"""[{"name":"{{name}}","md5":"{{FakeServer.Md5("x")}}"},{"name":"a.mul","md5":"{{FakeServer.Md5("new")}}"}]""";
        _server.Add("a.mul", "new");
        var service = CreateService(reservedPaths: ["TazUO Launcher"]);
        await service.CheckAsync();

        //Act
        var result = await service.DownloadAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.True(service.FilesVerified); //Not counted as a failure, it was never the file list's to update
        Assert.Equal(["a.mul"], _server.Downloads);
    }

    [Fact]
    public async Task FoldersThatOnlyShareAReservedPathsPrefixAreStillUpdated()
    {
        //Arrange
        _server.Add("TazUO Launcher Skins/skin.png", "art");
        var service = CreateService(reservedPaths: ["TazUO Launcher"]);
        await service.CheckAsync();

        //Act
        await service.DownloadAsync();

        //Assert
        Assert.Equal("art", ReadLocal(Path.Combine("TazUO Launcher Skins", "skin.png")));
    }

    [Fact]
    public async Task AServerThatIsNotHttpsIsNeverAsked()
    {
        //Arrange
        _server.Add("a.mul", "new");
        var errors = new List<UpdateErrorInfo>();
        var service = CreateService(url: "http://updates.test/");
        service.ErrorOccurred += errors.Add;

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Failed, result);
        Assert.Equal([new UpdateErrorInfo(UpdateError.InsecureServer)], errors);
        Assert.Equal(0, _server.Requests);
    }

    [Fact]
    public async Task AllowingInsecureDownloadsLetsAnHttpServerBeUsedStraightAway()
    {
        //Arrange
        _server.Add("a.mul", "new");
        var allowed = false;
        var service = CreateService(url: "http://updates.test/", allowInsecure: () => allowed);
        var refused = await service.CheckAsync();
        allowed = true; //The player ticks the setting, the launcher isn't restarted
        await service.CheckAsync();

        //Act
        var result = await service.DownloadAsync();

        //Assert
        Assert.Equal(UpdateResult.Failed, refused);
        Assert.Equal(UpdateResult.Finished, result);
        Assert.Equal("new", ReadLocal("a.mul"));
    }

    [Fact]
    public async Task AnAddressWithoutATrailingSlashWorksTheSame()
    {
        //Arrange
        _server.Add("maps/a.mul", "new");
        var service = CreateService(url: "https://updates.test");
        await service.CheckAsync();

        //Act
        var result = await service.DownloadAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.Equal(["/file/maps/a.mul"], _server.FilePaths);
    }

    [Fact]
    public async Task IgnoredFilesAndFoldersAreNeverDownloaded()
    {
        //Arrange
        _server.Add("map0.mul", "new");
        _server.Add("map1.mul", "new");
        _server.Add("Music/a.mp3", "new");
        _server.Add("Music/b.mp3", "new");
        WriteLocal("map0.mul", "my own edit");
        WriteLocal(IgnoreRules.FileName, "map0.mul\nMusic/\n");
        var service = CreateService();
        await service.CheckAsync();

        //Act
        var result = await service.DownloadAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.True(service.FilesVerified); //Ignoring is the player's choice, not a failure
        Assert.Equal(["map1.mul"], _server.Downloads);
        Assert.Equal("my own edit", ReadLocal("map0.mul"));
        Assert.Equal(["map0.mul", "Music/"], service.IgnoredItems); //The folder counts once
    }

    [Fact]
    public async Task AChangedFileThatIsIgnoredDoesntCountAsAnUpdate()
    {
        //Arrange
        _server.Add("map0.mul", "new");
        WriteLocal("map0.mul", "my own edit");
        WriteLocal(IgnoreRules.FileName, "map0.mul");
        var service = CreateService();

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.Equal(["map0.mul"], service.IgnoredItems);
    }

    [Fact]
    public async Task AFileIgnoredAfterTheCheckIsntDownloaded()
    {
        //Arrange
        _server.Add("map0.mul", "new");
        _server.Add("map1.mul", "new");
        var service = CreateService();
        await service.CheckAsync();
        WriteLocal(IgnoreRules.FileName, "map0.mul"); //Added in Settings before clicking Download

        //Act
        await service.DownloadAsync();

        //Assert
        Assert.Equal(["map1.mul"], _server.Downloads);
        Assert.Equal(["map0.mul"], service.IgnoredItems);
    }

    [Fact]
    public async Task TheServerCantReplaceThePlayersIgnoreList()
    {
        //Arrange
        _server.Add(IgnoreRules.FileName, "");
        _server.Add("map0.mul", "new");
        WriteLocal(IgnoreRules.FileName, "map0.mul");
        var service = CreateService();
        await service.CheckAsync();

        //Act
        await service.DownloadAsync();

        //Assert
        Assert.Empty(_server.Downloads);
        Assert.Equal("map0.mul", ReadLocal(IgnoreRules.FileName));
    }

    [Fact]
    public async Task CheckReportsPackagesThatNeedUpdating()
    {
        //Arrange
        _server.Add("a.mul", "same");
        WriteLocal("a.mul", "same");
        var packages = new FakePackageUpdater { Updates = FakePackageUpdater.TazUOPending };
        var service = CreateService(packages);

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.PackagesReady, result);
        Assert.Equal(0, packages.ApplyCount);
    }

    [Fact]
    public async Task FilesTakePrecedenceOverPackagesInTheCheckResult()
    {
        //Arrange
        _server.Add("a.mul", "new");
        WriteLocal("a.mul", "old");
        var service = CreateService(new FakePackageUpdater { Updates = FakePackageUpdater.TazUOPending });

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.UpdatesReady, result);
    }

    [Fact]
    public async Task DownloadAppliesPackagesBeforeDownloadingFiles()
    {
        //Arrange
        _server.Add("a.mul", "new");
        var order = new List<string>();
        var packages = new FakePackageUpdater { Updates = FakePackageUpdater.TazUOPending, OnApply = () => order.Add("packages") };
        var service = CreateService(packages);
        service.FileProgressChanged += file => { if (!order.Contains("files")) order.Add("files"); };
        await service.CheckAsync();

        //Act
        var result = await service.DownloadAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.Equal(["packages", "files"], order);
        Assert.Equal(1, packages.ApplyCount); //Finish doesn't apply them a second time
        Assert.Equal("new", ReadLocal("a.mul"));
    }

    [Fact]
    public async Task FinishAppliesPackagesWhenNothingWasPending()
    {
        //Arrange
        _server.Add("a.mul", "same");
        WriteLocal("a.mul", "same");
        var packages = new FakePackageUpdater(); //Nothing out of date, but profiles still get set up
        var service = CreateService(packages);

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.Equal(1, packages.ApplyCount);
    }

    [Fact]
    public async Task UntrustedPackagesFailTheCheck()
    {
        //Arrange
        _server.Add("a.mul", "same");
        WriteLocal("a.mul", "same");
        var service = CreateService(new FakePackageUpdater { CheckError = new UpdateServerException(UpdateError.PackagesUntrusted, "bad signature") });
        var errors = new List<UpdateErrorInfo>();
        service.ErrorOccurred += errors.Add;

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Failed, result);
        Assert.Equal([new UpdateErrorInfo(UpdateError.PackagesUntrusted)], errors);
    }

    [Fact]
    public async Task PackageErrorsAreReported()
    {
        //Arrange
        _server.Add("a.mul", "same");
        WriteLocal("a.mul", "same");
        var packages = new FakePackageUpdater { ApplyError = UpdateError.TazUOInstallFailed };
        var service = CreateService(packages);
        var errors = new List<UpdateErrorInfo>();
        service.ErrorOccurred += errors.Add;

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result); //Game files are fine, the launcher install is retried next time
        Assert.Equal([new UpdateErrorInfo(UpdateError.TazUOInstallFailed)], errors);
    }

    [Fact]
    public async Task CancelledCheckReturnsCancelled()
    {
        //Arrange
        _server.Add("a.mul", "new");
        var service = CreateService();
        service.ProgressChanged += _ => service.Cancel(); //Cancel as soon as the check starts

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Cancelled, result);
        Assert.Empty(_server.Downloads);
    }

    [Fact]
    public async Task DownloadAfterCancelledCheckFetchesAFreshFileList()
    {
        //Arrange
        _server.Add("a.mul", "new");
        var service = CreateService();
        void CancelOnce(UpdateProgress _)
        {
            service.ProgressChanged -= CancelOnce;
            service.Cancel();
        }
        service.ProgressChanged += CancelOnce;
        await service.CheckAsync();

        //Act
        var result = await service.DownloadAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.Equal("new", ReadLocal("a.mul"));
    }

    [Fact]
    public async Task FilesThatKeepFailingAreReported()
    {
        //Arrange
        _server.Add("a.mul", "new");
        _server.FailuresBeforeSuccess = 5;
        var service = CreateService();
        await service.CheckAsync();

        //Act
        var result = await service.DownloadAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.False(service.FilesVerified);
        Assert.Equal("a.mul", Assert.Single(service.FailedFiles));
    }

    [Fact]
    public async Task NamesWithSpecialCharactersDownload()
    {
        //Arrange
        _server.Add("maps/my map #1.mul", "content");
        var service = CreateService();
        await service.CheckAsync();

        //Act
        var result = await service.DownloadAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.True(service.FilesVerified);
        Assert.Equal("content", ReadLocal(Path.Combine("maps", "my map #1.mul")));
    }

    [Fact]
    public async Task KeptLocalFilesAreNotReplaced()
    {
        //Arrange
        _server.Add("user.cfg", "server default");
        WriteLocal("user.cfg", "player's own");
        var service = CreateService(keepLocalFiles: ["*.cfg"]);

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.Equal("player's own", ReadLocal("user.cfg"));
    }

    [Fact]
    public async Task MissingKeptLocalFilesAreDownloaded()
    {
        //Arrange
        _server.Add("user.cfg", "server default");
        var service = CreateService(keepLocalFiles: ["*.cfg"]);
        await service.CheckAsync();

        //Act
        await service.DownloadAsync();

        //Assert
        Assert.Equal("server default", ReadLocal("user.cfg"));
    }

    [Fact]
    public async Task PartialDownloadIsResumed()
    {
        //Arrange
        _server.Add("a.mul", "0123456789");
        WriteLocal("a.mul.part", "01234");
        var service = CreateService();
        await service.CheckAsync();

        //Act
        await service.DownloadAsync();

        //Assert
        Assert.Equal(5, Assert.Single(_server.RangeStarts));
        Assert.Equal("0123456789", ReadLocal("a.mul"));
        Assert.False(_fileSystem.File.Exists(_fileSystem.Path.Combine(InstallPath, "a.mul.part")));
    }

    [Fact]
    public async Task DamagedPartialDownloadStartsOver()
    {
        //Arrange
        _server.Add("a.mul", "0123456789");
        WriteLocal("a.mul.part", "XXXXX");
        var service = CreateService();
        await service.CheckAsync();

        //Act
        await service.DownloadAsync();

        //Assert
        Assert.Equal(new long?[] { 5, null }, _server.RangeStarts);
        Assert.Equal("0123456789", ReadLocal("a.mul"));
    }

    [Fact]
    public async Task UnchangedFilesUseTheCachedHash()
    {
        //Arrange
        _server.Add("a.mul", "same");
        WriteLocal("a.mul", "same");
        await CreateService().CheckAsync(); //Hashes a.mul and saves the cache
        var cachePath = _fileSystem.Path.Combine(InstallPath, HashCache.FileName);
        _fileSystem.File.WriteAllText(cachePath,
            _fileSystem.File.ReadAllText(cachePath).Replace(FakeServer.Md5("same"), FakeServer.Md5("other")));

        //Act
        var result = await CreateService().CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.UpdatesReady, result); //Trusted the (tampered) cache instead of re-hashing
    }

    [Fact]
    public async Task ChangedFilesAreHashedAgain()
    {
        //Arrange
        _server.Add("a.mul", "new");
        WriteLocal("a.mul", "old");
        await CreateService().CheckAsync();
        WriteLocal("a.mul", "new");
        _fileSystem.File.SetLastWriteTimeUtc(_fileSystem.Path.Combine(InstallPath, "a.mul"), DateTime.UtcNow.AddMinutes(1));

        //Act
        var result = await CreateService().CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
    }

    [Fact]
    public async Task ProgressCountsBytesWhenTheServerSendsSizes()
    {
        //Arrange
        _server.IncludeSizes = true;
        _server.Add("a.mul", "12345");
        _server.Add("b.mul", "1234567890");
        var service = CreateService();
        await service.CheckAsync();
        var progress = new List<UpdateProgress>();
        service.ProgressChanged += p => { if (p.Phase == UpdatePhase.Downloading) lock (progress) progress.Add(p); };

        //Act
        await service.DownloadAsync();

        //Assert
        var last = progress.Last();
        Assert.Equal(15, last.BytesTotal);
        Assert.Equal(15, last.BytesDone);
        Assert.Equal(100, last.Percent);
    }
}
