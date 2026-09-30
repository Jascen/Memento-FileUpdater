using System.Diagnostics;
using System.IO.Abstractions;
using System.IO.Compression;

namespace FileUpdaterClient.Updating;

//Replaces the running launcher with a downloaded package. The running exe can't be overwritten on Windows, so the launcher
//starts a temporary copy of itself with --apply-update and exits. That copy waits for this process to end, swaps the files in
//(LauncherSwap) and starts the new launcher. Program.Main hands the --apply-update run to RunApply.
public class SelfUpdater : ISelfUpdater
{
    public const string ApplyArgument = "--apply-update";
    private const string TempFolderPrefix = "launcher-update-";

    public Task<bool> ApplyAndRestartAsync(string packagePath, Version newVersion, CancellationToken cancellationToken)
    {
        var exePath = Environment.ProcessPath;
        //Running through `dotnet run` or similar: the "exe" is dotnet itself and must not be overwritten
        if (string.IsNullOrEmpty(exePath) || Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Launcher {newVersion} was downloaded, but this launcher isn't a standalone exe so it can't replace itself.");
            return Task.FromResult(false);
        }

        var appDirectory = Path.GetDirectoryName(exePath)!;
        if (!CanWrite(appDirectory))
        {
            Console.WriteLine($"Launcher {newVersion} was downloaded, but {appDirectory} isn't writable.");
            return Task.FromResult(false);
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), TempFolderPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        foreach (var file in Directory.EnumerateFiles(appDirectory).Where(file => IsLauncherFile(file, exePath)))
            File.Copy(file, Path.Combine(tempDirectory, Path.GetFileName(file)), overwrite: true);

        var applier = Path.Combine(tempDirectory, Path.GetFileName(exePath));
        MakeExecutable(applier);

        Process.Start(new ProcessStartInfo(applier, Arguments(Environment.ProcessId, packagePath, appDirectory, exePath))
        {
            WorkingDirectory = tempDirectory,
            //On Windows, ShellExecute starts it outside this process's job so it survives our exit. Elsewhere it has to exec directly
            UseShellExecute = OperatingSystem.IsWindows(),
        });
        return Task.FromResult(true);
    }

    //Runs in the temporary copy. Returns the process exit code
    public static int RunApply(string[] args)
    {
        if (args.Length < 5 || !int.TryParse(args[1], out var pid)) return 1;
        var zipPath = Path.GetFullPath(args[2]);
        var appDirectory = Path.GetFullPath(args[3]);
        var exePath = Path.GetFullPath(args[4]);

        try
        {
            using var launcher = Process.GetProcessById(pid);
            if (!launcher.WaitForExit(10_000)) return 3; //Still running, so nothing is swapped and it carries on as it was
        }
        catch (ArgumentException)
        {
            //Already gone
        }
        Thread.Sleep(500); //Let the OS release the exe

        try
        {
            new LauncherSwap(new FileSystem(), (zip, folder) => ZipFile.ExtractToDirectory(zip, folder, overwriteFiles: true))
                .Apply(zipPath, appDirectory, exePath);
        }
        catch (Exception e)
        {
            //There is no window to show this in. The old launcher is started below either way
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "launcher-update-error.txt"), e.ToString());
        }

        try
        {
            File.Delete(zipPath);
        }
        catch
        {
            //A leftover download is harmless
        }

        MakeExecutable(exePath);
        Process.Start(new ProcessStartInfo(exePath)
        {
            WorkingDirectory = appDirectory,
            UseShellExecute = OperatingSystem.IsWindows(),
        });
        return 0;
    }

    //Removes temporary applier copies left by earlier updates. One that is still running is skipped, its files are locked
    public static void CleanUpTempFolders()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(Path.GetTempPath(), TempFolderPrefix + "*"))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    //Still in use, tried again on the next start
                }
            }
        }
        catch
        {
            //Not worth stopping the launcher for
        }
    }

    //The exe and the native libraries beside it, which is everything the single-file launcher needs to run
    private static bool IsLauncherFile(string file, string exePath) =>
        string.Equals(file, exePath, StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(file).ToLowerInvariant() is ".dll" or ".so" or ".dylib";

    //Quoted, and without a trailing separator, which would otherwise read as an escaped quote on Windows
    private static string Arguments(int pid, string zipPath, string appDirectory, string exePath) =>
        $"{ApplyArgument} {pid} \"{Path.GetFullPath(zipPath)}\" \"{Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory))}\" \"{Path.GetFullPath(exePath)}\"";

    private static bool CanWrite(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
