using FileUpdaterClient.Updating;

namespace FileUpdaterClient.Tests.Fakes;

//Stands in for the TazUO launcher install: "installs" by recording which zip it was given
public class FakeClientInstaller : IClientInstaller
{
    public bool IsInstalled { get; set; }
    public string? InstalledFrom { get; private set; }
    public string? InstalledContent { get; private set; }
    public int ProfileSetups { get; private set; }
    public Exception? InstallError { get; set; }

    public Func<string, string>? ReadZip { get; init; } //Lets the test look inside the "zip" while it still exists

    public void InstallFromZip(string zipPath)
    {
        if (InstallError != null) throw InstallError;
        InstalledFrom = zipPath;
        InstalledContent = ReadZip?.Invoke(zipPath);
        IsInstalled = true;
    }

    public void EnsureProfiles() => ProfileSetups++;
}

//Records what the launcher asked to apply, and answers as told
public class FakeSelfUpdater : ISelfUpdater
{
    public bool Restarts { get; set; }
    public Exception? Error { get; set; }
    public List<(string Path, Version Version)> Applied { get; } = new();

    public Func<string, string>? ReadZip { get; init; }
    public string? AppliedContent { get; private set; }

    public Task<bool> ApplyAndRestartAsync(string packagePath, Version newVersion, CancellationToken cancellationToken)
    {
        if (Error != null) throw Error;
        Applied.Add((packagePath, newVersion));
        AppliedContent = ReadZip?.Invoke(packagePath);
        return Task.FromResult(Restarts);
    }
}

public class FakePackageState : IPackageState
{
    public Version? ClientVersion { get; set; }

    public void SetClientVersion(Version version) => ClientVersion = version;
}
