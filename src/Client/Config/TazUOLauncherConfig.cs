namespace FileUpdaterClient.Config;

//Build-time configuration for the TazUO launcher: whether it's used, where it installs to, and the profiles it starts with.
//It is installed from the client package the server hosts (see LauncherConfig.TrustedPublicKeys).
//Its on-screen text lives in Theme/strings.json.
public static class TazUOLauncherConfig
{
    //When false the TazUO launcher is never downloaded and there is no Play Now button, the updater only keeps files up to date
    public const bool Enabled = true;

    public const string ExecutableName = "TazUOLauncher"; //Without .exe, which is added on Windows

    //Installed into this folder inside the install folder, with these profiles pre-created
    public const string InstallFolder = "TazUO Launcher";
    public static readonly TazUOProfile[] Profiles =
    [
        new("uodiablo-live", "UODiablo", "127.0.0.1", 2593, "7.0.15.1"),
        new("uodiablo-test", "UODiablo Test", "127.0.0.1", 2594, "7.0.15.1"),
    ];
}

//Id is also the file name TazUO stores the profile under, so keep it stable once released
public record TazUOProfile(string Id, string Name, string Ip, int Port, string ClientVersion);
