using System.IO.Abstractions.TestingHelpers;
using System.Net;
using FileUpdaterClient.Tests.Fakes;
using FileUpdaterClient.Updating;

namespace FileUpdaterClient.Tests.Updating;

//Runs against an in-memory file system and fake server, so nothing touches the disk or network
public class UpdateServiceTests
{
    private const string Url = "http://updates.test/";
    private static readonly string InstallPath = MockUnixSupport.Path(@"C:\game");
    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeServer _server = new();

    public UpdateServiceTests() => _fileSystem.Directory.CreateDirectory(InstallPath);

    private UpdateService CreateService(ILauncherInstaller? launcher = null)
    {
        var localFiles = new LocalFiles(_fileSystem);
        return new UpdateService(new FileServerClient(Url, localFiles, _server), localFiles, InstallPath, launcher);
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

    [Fact]
    public async Task CheckReportsMissingLauncher()
    {
        //Arrange
        _server.Add("a.mul", "same");
        WriteLocal("a.mul", "same");
        var launcher = new FakeLauncher();
        var service = CreateService(launcher);

        //Act
        var result = await service.CheckAsync();

        //Assert
        Assert.Equal(UpdateResult.LauncherReady, result);
        Assert.False(launcher.IsInstalled);
    }

    [Fact]
    public async Task DownloadInstallsMissingLauncher()
    {
        //Arrange
        _server.Add("a.mul", "same");
        WriteLocal("a.mul", "same");
        var launcher = new FakeLauncher();
        var service = CreateService(launcher);
        await service.CheckAsync();

        //Act
        var result = await service.DownloadAsync();

        //Assert
        Assert.Equal(UpdateResult.Finished, result);
        Assert.True(launcher.IsInstalled);
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
}
