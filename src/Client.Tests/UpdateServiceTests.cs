using System.Net;
using FileUpdaterClient.Updating;

namespace FileUpdaterClient.Tests;

public class UpdateServiceTests : IDisposable
{
    private const string Url = "http://updates.test/";
    private readonly string _installPath = Path.Combine(Path.GetTempPath(), "updater-tests-" + Guid.NewGuid());
    private readonly FakeServer _server = new();

    public UpdateServiceTests() => Directory.CreateDirectory(_installPath);

    public void Dispose() => Directory.Delete(_installPath, recursive: true);

    private UpdateService CreateService(ILauncherInstaller? launcher = null) =>
        new(new FileServerClient(Url, _server), _installPath, launcher);

    private void WriteLocal(string name, string content)
    {
        var path = Path.Combine(_installPath, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task CheckFinishesWhenFilesMatch()
    {
        _server.Add("a.mul", "same");
        WriteLocal("a.mul", "same");

        var service = CreateService();
        Assert.Equal(UpdateResult.Finished, await service.CheckAsync());
        Assert.True(service.FilesVerified);
        Assert.Empty(_server.Downloads);
    }

    [Fact]
    public async Task CheckReportsUpdatesWithoutDownloading()
    {
        _server.Add("a.mul", "new");
        WriteLocal("a.mul", "old");

        var service = CreateService();
        Assert.Equal(UpdateResult.UpdatesReady, await service.CheckAsync());
        Assert.False(service.FilesVerified);
        Assert.Empty(_server.Downloads);
    }

    [Fact]
    public async Task DownloadReplacesChangedAndMissingFiles()
    {
        _server.Add("a.mul", "new");
        _server.Add("maps/b.mul", "missing");
        WriteLocal("a.mul", "old");

        var service = CreateService();
        await service.CheckAsync();
        Assert.Equal(UpdateResult.Finished, await service.DownloadAsync());

        Assert.True(service.FilesVerified);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_installPath, "a.mul")));
        Assert.Equal("missing", File.ReadAllText(Path.Combine(_installPath, "maps", "b.mul")));
    }

    [Fact]
    public async Task DownloadRetriesFailedFiles()
    {
        _server.Add("a.mul", "new");
        _server.FailuresBeforeSuccess = 1;

        var service = CreateService();
        await service.CheckAsync();
        Assert.Equal(UpdateResult.Finished, await service.DownloadAsync());
        Assert.Equal(2, _server.Downloads.Count);
        Assert.True(service.FilesVerified);
    }

    [Fact]
    public async Task ServerErrorFailsTheCheck()
    {
        _server.ListStatus = HttpStatusCode.InternalServerError;
        var errors = new List<UpdateErrorInfo>();

        var service = CreateService();
        service.ErrorOccurred += errors.Add;
        Assert.Equal(UpdateResult.Failed, await service.CheckAsync());
        Assert.Equal(UpdateError.ConnectionFailed, Assert.Single(errors).Error);
    }

    [Fact]
    public async Task MalformedListIsBadData()
    {
        _server.RawList = "not json";
        var errors = new List<UpdateErrorInfo>();

        var service = CreateService();
        service.ErrorOccurred += errors.Add;
        Assert.Equal(UpdateResult.Failed, await service.CheckAsync());
        Assert.Equal(UpdateError.BadData, Assert.Single(errors).Error);
    }

    [Fact]
    public async Task NamesOutsideInstallFolderAreSkipped()
    {
        _server.RawList = $$"""[{"name":"../evil.txt","md5":"{{FakeServer.Md5("x")}}"}]""";

        var service = CreateService();
        Assert.Equal(UpdateResult.Finished, await service.CheckAsync());
        Assert.Empty(_server.Downloads);
    }

    [Fact]
    public async Task MissingLauncherIsReportedThenInstalled()
    {
        _server.Add("a.mul", "same");
        WriteLocal("a.mul", "same");
        var launcher = new FakeLauncher();

        var service = CreateService(launcher);
        Assert.Equal(UpdateResult.LauncherReady, await service.CheckAsync());
        Assert.Equal(UpdateResult.Finished, await service.DownloadAsync());
        Assert.True(launcher.IsInstalled);
    }

    [Fact]
    public async Task CancelledCheckStartsFromAFreshFileList()
    {
        _server.Add("a.mul", "new");
        var service = CreateService();
        service.ProgressChanged += _ => service.Cancel(); //Cancel as soon as the check starts

        Assert.Equal(UpdateResult.Cancelled, await service.CheckAsync());

        var fresh = CreateService();
        Assert.Equal(UpdateResult.UpdatesReady, await fresh.CheckAsync());
    }

    private class FakeLauncher : ILauncherInstaller
    {
        public bool IsInstalled { get; private set; }

        public Task EnsureInstalledAsync(CancellationToken cancellationToken)
        {
            IsInstalled = true;
            return Task.CompletedTask;
        }
    }
}
