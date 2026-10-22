using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PoorMansFeed;

public sealed partial class HelpDialog : Window
{
    public HelpDialog()
    {
        AvaloniaXamlLoader.Load(this);
        // These three are methods on AppText (they embed live numbers), so they are set here rather than through x:Static
        if (this.FindControl<TextBlock>("DatasetText") is { } datasetText)
        {
            datasetText.Text = AppText.HelpDatasetText();
        }

        if (this.FindControl<TextBlock>("SearchText") is { } searchText)
        {
            searchText.Text = AppText.HelpSearchText();
        }

        if (this.FindControl<TextBlock>("AbuseText") is { } abuseText)
        {
            abuseText.Text = AppText.HelpAbuseText();
        }

        // Close is the only focusable control, so the scroll keys are caught on their way down to it and move the sections
        AddHandler(KeyDownEvent, ScrollKeyDown, RoutingStrategies.Tunnel);
    }

    private void ScrollKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || this.FindControl<ScrollViewer>("HelpScroll") is not { } scroll)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
                scroll.LineUp();
                break;
            case Key.Down:
                scroll.LineDown();
                break;
            case Key.PageUp:
                scroll.PageUp();
                break;
            case Key.PageDown:
                scroll.PageDown();
                break;
            case Key.Home:
                scroll.ScrollToHome();
                break;
            case Key.End:
                scroll.ScrollToEnd();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}
