using FileUpdaterClient.Updating;
using FileUpdaterPackages;

namespace FileUpdaterClient.Tests.Fakes;

//Stands in for PackageUpdater when testing UpdateService: reports what it's told to and records that it was applied
public class FakePackageUpdater : IPackageUpdater
{
    public static readonly PackageUpdates ClientPending =
        new(null, new PackageEntry(PackageRole.Client, "1.0.0", "win-x64", "client-1.0.0.win-x64.zip", "abc", 1), false);

    public static readonly PackageUpdates LauncherPending =
        new(new PackageEntry(PackageRole.Launcher, "2.0.0", "win-x64", "launcher-2.0.0.win-x64.zip", "abc", 1), null, false);

    public PackageUpdates Updates { get; set; } = PackageUpdates.None;
    public Exception? CheckError { get; set; }
    public UpdateError? ApplyError { get; set; } //Reported through the error callback, like a failed install
    public bool Restart { get; set; } //Apply reports the launcher replaced itself
    public Action? OnApply { get; set; }
    public int ApplyCount { get; private set; }

    public Task<PackageUpdates> CheckAsync(CancellationToken cancellationToken) =>
        CheckError == null ? Task.FromResult(Updates) : Task.FromException<PackageUpdates>(CheckError);

    public Task<bool> ApplyAsync(PackageUpdates updates, Action<UpdateProgress> progress, Action<UpdateErrorInfo> error,
        CancellationToken cancellationToken)
    {
        ApplyCount++;
        OnApply?.Invoke();
        if (ApplyError is { } applyError) error(new UpdateErrorInfo(applyError));
        return Task.FromResult(Restart);
    }
}
