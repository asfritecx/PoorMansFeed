using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PoorMansFeed;

/// <summary>Shows the ModSecurity rules for one feed's lists, bound to a <see cref="ModSecurityRulesModel"/>.</summary>
public sealed partial class ModSecurityRulesDialog : Window
{
    public ModSecurityRulesDialog() => InitializeComponent();

    internal ModSecurityRulesDialog(ModSecurityRulesModel model) : this()
    {
        ArgumentNullException.ThrowIfNull(model);
        DataContext = model;

        // On the two scrolled texts alone, not the window, so the location box keeps its arrows, Home and End
        this.FindControl<ScrollViewer>("NotesScroll")!.KeyDown += ScrollKeyDown;
        this.FindControl<ScrollViewer>("ProblemScroll")!.KeyDown += ScrollKeyDown;
        Opened += (_, _) => this.FindControl<TextBox>("LocationBox")!.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private static void ScrollKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ScrollViewer scroll || e.KeyModifiers != KeyModifiers.None)
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
