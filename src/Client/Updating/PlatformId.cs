using System.Reflection;
using System.Runtime.InteropServices;

namespace FileUpdaterClient.Updating;

//Which platform this launcher runs on, named like the packages on the server (win-x64, linux-x64, osx-x64, osx-arm64)
public static class PlatformId
{
    public static string Current
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            return $"{os}-{arch}";
        }
    }
}

//The version of this launcher, from the <Version> it was built with (the release workflow sets it from the tag)
public static class LauncherVersion
{
    public static Version Current
    {
        get
        {
            var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var core = info?.Split('+', '-')[0]; //Drop build metadata like 1.2.0+abc123
            return Version.TryParse(core, out var version) ? version : new Version(0, 0, 0);
        }
    }
}
