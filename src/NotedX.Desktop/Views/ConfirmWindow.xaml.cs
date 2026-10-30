using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace NotedX.Desktop.Views;

/// <summary>Themed yes/no dialog that keeps confirmation prompts inside the app's visual language.</summary>
public partial class ConfirmWindow
{
    /// <summary>Creates the dialog for the supplied question.</summary>
    public ConfirmWindow(string title, string message, string confirmText, bool isDestructive)
    {
        InitializeComponent();

        Title = title;
        HeaderBar.Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;

        if (isDestructive)
        {
            ConfirmButton.Appearance = ControlAppearance.Danger;
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }
}
