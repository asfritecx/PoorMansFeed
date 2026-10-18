using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace GraphiteUi;

public sealed partial class ConfirmationDialog : Window
{
    public ConfirmationDialog()
    {
        InitializeComponent();
    }

    /// <param name="showCancel">False for a notice with a single button.</param>
    internal ConfirmationDialog(string title, string message, string confirmText, bool showCancel = true) : this()
    {
        Title = title;
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
        this.FindControl<Button>("ConfirmButton")!.Content = confirmText;
        this.FindControl<Button>("CancelButton")!.IsVisible = showCancel;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void ConfirmClicked(object? sender, RoutedEventArgs e) => Close(true);

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}
