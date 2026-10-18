using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PoorMansFeed;

public sealed partial class HelpDialog : Window
{
    public HelpDialog()
    {
        AvaloniaXamlLoader.Load(this);
        // These two are methods on AppText (they embed live numbers), so they are set here rather than through x:Static
        if (this.FindControl<TextBlock>("DatasetText") is { } datasetText)
        {
            datasetText.Text = AppText.HelpDatasetText();
        }

        if (this.FindControl<TextBlock>("SearchText") is { } searchText)
        {
            searchText.Text = AppText.HelpSearchText();
        }
    }

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}
