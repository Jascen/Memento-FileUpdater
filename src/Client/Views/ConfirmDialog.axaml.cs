using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FileUpdaterClient.Views;

//Themed yes/no popup. ShowDialog<bool> returns true when the player confirms
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string title, string message, string confirmText, string cancelText) : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        CancelButton.IsVisible = cancelText.Length > 0; //Just an OK button when there is nothing to cancel
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
