using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FileUpdaterClient;

//Installs the TazUO launcher next to the game files and gives it ready-made profiles for our shard.
//Runs after the file update. The TazUO launcher keeps itself and the TazUO client up to date from then on.
public static class TazUOSetup
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/PlayTazUO/TUO-Launcher/releases/latest";
    private const string LauncherExeName = "TazUOLauncher";

    public static string LauncherDirectory => Path.Combine(InstallLocation.Path, Settings.TazUOLauncherFolder);

    public static string LauncherExecutable => Path.Combine(LauncherDirectory,
        LauncherExeName + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty));

    public static bool IsInstalled => File.Exists(LauncherExecutable);

    public static async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalled)
            await DownloadLauncherAsync(cancellationToken);

        foreach (var profile in Settings.TazUOProfiles)
            CreateProfileIfMissing(profile);
    }

    private static async Task DownloadLauncherAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FileUpdaterClient"); //GitHub rejects requests without one

        //Release assets are named like TazUO-Launcher.win-x64.zip
        var assetSuffix = $".{GetRuntimeId()}.zip";
        var release = JsonNode.Parse(await client.GetStringAsync(ReleaseApiUrl, cancellationToken));
        var downloadUrl = release?["assets"]?.AsArray()
            .Select(asset => asset?["browser_download_url"]?.GetValue<string>())
            .FirstOrDefault(url => url != null && url.EndsWith(assetSuffix, StringComparison.OrdinalIgnoreCase));

        if (downloadUrl == null)
            throw new InvalidOperationException($"No TazUO launcher download found for {assetSuffix}");

        Console.WriteLine($"Downloading TazUO launcher from {downloadUrl}..");
        var zipPath = Path.Combine(InstallLocation.Path, "TazUO-Launcher.zip.part");
        try
        {
            await using (var zipStream = await client.GetStreamAsync(downloadUrl, cancellationToken))
            await using (var fileStream = File.Create(zipPath))
            {
                await zipStream.CopyToAsync(fileStream, cancellationToken);
            }

            ZipFile.ExtractToDirectory(zipPath, LauncherDirectory, overwriteFiles: true);
        }
        finally
        {
            File.Delete(zipPath);
        }

        if (!OperatingSystem.IsWindows() && File.Exists(LauncherExecutable))
        {
            File.SetUnixFileMode(LauncherExecutable, File.GetUnixFileMode(LauncherExecutable)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    //Writes the two files the TazUO launcher reads for a profile: Profiles/<id>.json and Profiles/Settings/<id>.json.
    //Existing profiles are left alone so changes players make in the TazUO launcher are kept.
    private static void CreateProfileIfMissing(TazUOProfile profile)
    {
        var profilesDir = Path.Combine(LauncherDirectory, "Profiles");
        var settingsDir = Path.Combine(profilesDir, "Settings");
        var profilePath = Path.Combine(profilesDir, profile.Id + ".json");
        if (File.Exists(profilePath)) return;

        Directory.CreateDirectory(settingsDir);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var clientSettings = new JsonObject
        {
            ["ip"] = profile.Ip,
            ["port"] = profile.Port,
            ["ultimaonlinedirectory"] = InstallLocation.Path, //The game files this updater downloads
            ["clientversion"] = profile.ClientVersion,
            ["lastservernum"] = 1,
        };
        File.WriteAllText(Path.Combine(settingsDir, profile.Id + ".json"), clientSettings.ToJsonString(options));

        var launcherProfile = new JsonObject
        {
            ["Name"] = profile.Name,
            ["SettingsFile"] = profile.Id,
            ["FileName"] = profile.Id,
            ["LastCharacterName"] = string.Empty,
            ["AdditionalArgs"] = string.Empty,
        };
        File.WriteAllText(profilePath, launcherProfile.ToJsonString(options));
        Console.WriteLine($"Created TazUO profile [{profile.Name}]");
    }

    private static string GetRuntimeId()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return $"{os}-{arch}";
    }
}

//Id is also the file name TazUO stores the profile under, so keep it stable once released
public record TazUOProfile(string Id, string Name, string Ip, int Port, string ClientVersion);
