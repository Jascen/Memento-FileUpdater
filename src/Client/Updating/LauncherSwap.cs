using System.IO.Abstractions;

namespace FileUpdaterClient.Updating;

//Puts the files of a downloaded launcher package over the installed launcher.
//Everything is unpacked to a temporary folder first, so a bad zip or a full disk is found before the install is touched,
//and the exe is copied last, so a copy that fails partway leaves an exe that still starts.
public class LauncherSwap(IFileSystem fileSystem, Action<string, string> extract)
{
    //extract(zipPath, folder) unpacks a zip into a folder. Tests fake it, since zips are read from the real disk
    public void Apply(string zipPath, string appDirectory, string exePath)
    {
        var staging = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "launcher-staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            fileSystem.Directory.CreateDirectory(staging);
            extract(zipPath, staging);

            var exeName = fileSystem.Path.GetFileName(exePath);
            var staged = fileSystem.Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .OrderBy(file => IsExe(file, exeName)) //false sorts first, so the exe comes last
                .ToList();

            if (staged.Count == 0) throw new InvalidDataException("The launcher package was empty.");
            if (!staged.Any(file => IsExe(file, exeName)))
                throw new InvalidDataException($"The launcher package doesn't contain {exeName}.");

            foreach (var file in staged)
            {
                var target = fileSystem.Path.Combine(appDirectory, fileSystem.Path.GetRelativePath(staging, file));
                fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(target)!);
                fileSystem.File.Copy(file, target, overwrite: true);
            }
        }
        finally
        {
            try
            {
                if (fileSystem.Directory.Exists(staging)) fileSystem.Directory.Delete(staging, recursive: true);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Couldn't remove {staging}: {e.Message}"); //Only leftover temp files
            }
        }
    }

    private bool IsExe(string file, string exeName) =>
        string.Equals(fileSystem.Path.GetFileName(file), exeName, StringComparison.OrdinalIgnoreCase);
}
