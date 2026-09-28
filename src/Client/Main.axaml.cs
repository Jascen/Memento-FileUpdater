using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace FileUpdaterClient;

public partial class Main : Window
{
    private MainViewModel _data;
    private bool _folderReady;

    public Main()
    {
        InitializeComponent();
        DataContext = _data = new MainViewModel();
        Title = _data.Title;
        Opened += async (_, _) => await StartWhenFolderChosen();
    }

    private async Task StartWhenFolderChosen()
    {
        InstallLocation.Load();
        Preferences.Load();
        if (!InstallLocation.EnsureUsable(out var error))
        {
            //Ask for another folder in Settings before checking anything
            _data.ErrorMessage = error;
            _data.ProgressText = Settings.NoFolderChosen;
            await OpenSettings(error);
            return;
        }

        await StartUpdate();
    }

    private async Task StartUpdate()
    {
        _folderReady = true;
        _data.LauncherInstalled = TazUOSetup.IsInstalled;

        if (Preferences.Current.VerifyOnLaunch)
            await UpdateHandler.HandleUpdates(_data);
        else
            _data.ProgressText = Settings.NotVerified;
    }

    //The center button downloads pending updates first, then launches the game
    private async void MainButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_data.DownloadsReady)
            await UpdateHandler.DownloadUpdates();
        else
            await Play();
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e) => await OpenSettings();

    private async Task OpenSettings(string folderError = "")
    {
        var dialog = new SettingsDialog();
        dialog.ShowFolderError(folderError);
        var newFolder = await ShowModal(dialog.ShowDialog<string?>(this));
        if (newFolder == null) return;

        if (!InstallLocation.TrySet(newFolder, out var error))
        {
            _data.ErrorMessage = error;
            return;
        }

        _data.ErrorMessage = string.Empty;
        if (!_folderReady)
        {
            //The default folder wasn't usable, so start now that one is picked
            await StartUpdate();
            return;
        }

        //The updater runs once per launch, so restart it to check the new folder
        UpdateHandler.Cancel();
        if (Environment.ProcessPath != null) Process.Start(Environment.ProcessPath);
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    //Dims the launcher while a dialog is open
    private async Task<T> ShowModal<T>(Task<T> dialog)
    {
        _data.IsDialogOpen = true;
        try
        {
            return await dialog;
        }
        finally
        {
            _data.IsDialogOpen = false;
        }
    }

    // The window has no system title bar, so let it be dragged from anywhere that isn't a control
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void NavLink_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not NavLink link) return;

        if (link.Target == NavLink.VerifyAction)
        {
            if (_folderReady) _ = UpdateHandler.HandleUpdates(_data);
        }
        else
            OpenUrl(link.Target);
    }

    //Opens the TazUO launcher, first asking the player to confirm if the files weren't fully verified
    private async Task Play()
    {
        if (!_data.FilesVerified && Preferences.Current.WarnIfNotVerified)
        {
            var dialog = new ConfirmDialog(Settings.UnverifiedTitle, Settings.UnverifiedMessage,
                Settings.PlayAnyway, Settings.CancelText);
            if (!await ShowModal(dialog.ShowDialog<bool>(this))) return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(TazUOSetup.LauncherExecutable)
            {
                UseShellExecute = true,
                WorkingDirectory = TazUOSetup.LauncherDirectory
            });
        }
        catch (Exception ex)
        {
            _data.ErrorMessage = Settings.LaunchError;
            Console.WriteLine(ex.Message);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => UpdateHandler.Cancel();

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            _ = Launcher.LaunchUriAsync(uri);
    }
}
