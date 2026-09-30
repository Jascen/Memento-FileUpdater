using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileUpdaterClient.Config;
using FileUpdaterClient.Updating;

namespace FileUpdaterClient.TazUO;

//Installs the TazUO launcher (from the package the server hosts) next to the game files, gives it ready-made profiles
//for our shard, and starts it. PackageUpdater decides when to install; this class only knows how.
public class TazUOLauncher(string installPath) : IClientInstaller
{
    public string LauncherDirectory => Path.Combine(installPath, TazUOLauncherConfig.InstallFolder);

    public string LauncherExecutable => Path.Combine(LauncherDirectory,
        TazUOLauncherConfig.ExecutableName + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty));

    public bool IsInstalled => File.Exists(LauncherExecutable);

    public void Start()
    {
        Process.Start(new ProcessStartInfo(LauncherExecutable)
        {
            UseShellExecute = true,
            WorkingDirectory = LauncherDirectory
        });
    }

    //Unpacks a package zip into the launcher folder, replacing what's there. Players' profiles aren't in the zip, so they are kept
    public void InstallFromZip(string zipPath)
    {
        ZipFile.ExtractToDirectory(zipPath, LauncherDirectory, overwriteFiles: true);

        if (!OperatingSystem.IsWindows() && File.Exists(LauncherExecutable))
        {
            File.SetUnixFileMode(LauncherExecutable, File.GetUnixFileMode(LauncherExecutable)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    public void EnsureProfiles()
    {
        foreach (var profile in TazUOLauncherConfig.Profiles)
            CreateProfileIfMissing(profile);
    }

    //Writes the two files the TazUO launcher reads for a profile: Profiles/<id>.json and Profiles/Settings/<id>.json.
    //Existing profiles are left alone so changes players make in the TazUO launcher are kept.
    private void CreateProfileIfMissing(TazUOProfile profile)
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
            ["ultimaonlinedirectory"] = installPath, //The game files this updater downloads
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
}
