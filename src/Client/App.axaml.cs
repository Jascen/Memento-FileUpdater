using System.IO.Abstractions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FileUpdaterClient.Config;
using FileUpdaterClient.TazUO;
using FileUpdaterClient.Updating;
using FileUpdaterClient.UserSettings;
using FileUpdaterClient.ViewModels;
using FileUpdaterClient.Views;

namespace FileUpdaterClient;

public partial class App : Application
{
    //Where launcher and TazUO packages are saved while they download
    private static readonly string PackageDownloadFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LauncherConfig.AppDataFolder, "downloads");

    private DispatcherTimer? _launcherCheckTimer;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = CreateViewModel();
            desktop.MainWindow = new MainWindow(viewModel);
            desktop.Exit += (_, _) => viewModel.CancelUpdate();
            StartLauncherChecks(viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    //Builds the real services and hands them to the view model, which creates none of its own
    public static MainViewModel CreateViewModel()
    {
        var localFiles = new LocalFiles(new FileSystem());
        var server = new FileServerClient(LauncherConfig.UpdateUrl, localFiles,
            allowInsecure: () => Preferences.Current.AllowInsecureDownloads);
        var downloader = new PackageDownloader(server, localFiles, PackageDownloadFolder);
        var trustedKeys = LauncherConfig.TrustedPublicKeys;

        //Without a trusted signing key nothing the server hosts as a package is ever installed
        var launcherUpdater = trustedKeys.Length == 0 ? null
            : new LauncherUpdater(server, trustedKeys, PlatformId.Current, LauncherVersion.Current, downloader, new SelfUpdater());

        InstallSession OpenFolder(string installPath)
        {
            var tazUO = TazUOLauncherConfig.Enabled ? new TazUOLauncher(installPath) : null;
            var packages = trustedKeys.Length == 0 ? null
                : new PackageUpdater(server, trustedKeys, PlatformId.Current, downloader, tazUO, new PackageState());
            //The TazUO launcher only ever comes from its signed package, never from the file list
            string[] reservedPaths = tazUO == null ? [] : [TazUOLauncherConfig.InstallFolder];
            var updates = new UpdateService(server, localFiles, installPath, packages, LauncherConfig.KeepLocalFiles, reservedPaths);
            return new InstallSession(updates, tazUO);
        }

        return new MainViewModel(new SavedSettings(), OpenFolder, launcherUpdater, new AvaloniaUiThread());
    }

    //Now and then while the launcher is open, looks for a newer version of itself. The check at launch is the view model's
    private void StartLauncherChecks(MainViewModel viewModel)
    {
        if (LauncherConfig.TrustedPublicKeys.Length == 0) return;

        _launcherCheckTimer = new DispatcherTimer { Interval = LauncherConfig.PackageCheckInterval };
        _launcherCheckTimer.Tick += async (_, _) => await viewModel.RefreshLauncherUpdateAsync();
        _launcherCheckTimer.Start();
    }

    private sealed class AvaloniaUiThread : IUiThread
    {
        public void Post(Action action) => Dispatcher.UIThread.Post(action);

        public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}
