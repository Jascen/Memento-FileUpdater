using FileUpdaterClient.Updating;

namespace FileUpdaterClient.Tests.Fakes;

//Stands in for the TazUO launcher: "installs" instantly
public class FakeLauncher : ILauncherInstaller
{
    public bool IsInstalled { get; private set; }

    public Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        IsInstalled = true;
        return Task.CompletedTask;
    }
}
