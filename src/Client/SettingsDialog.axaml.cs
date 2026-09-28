using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FileUpdaterClient;

//Edits a copy of the saved preferences so Cancel leaves them untouched
public partial class SettingsDialog : Window
{
    private readonly Preferences _edited;

    public SettingsDialog()
    {
        InitializeComponent();
        DataContext = _edited = new Preferences
        {
            VerifyOnLaunch = Preferences.Current.VerifyOnLaunch,
            WarnIfNotVerified = Preferences.Current.WarnIfNotVerified,
        };
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Preferences.Save(_edited);
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
