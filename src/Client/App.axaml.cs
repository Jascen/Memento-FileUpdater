using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FileUpdaterClient.ViewModels;
using FileUpdaterClient.Views;

namespace FileUpdaterClient;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow(viewModel);
            desktop.Exit += (_, _) => viewModel.CancelUpdate();
        }

        base.OnFrameworkInitializationCompleted();
    }

    //Starts a fresh copy of the updater and closes this one
    public static void Restart()
    {
        if (Environment.ProcessPath != null) Process.Start(Environment.ProcessPath);
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
