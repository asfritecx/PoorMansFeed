using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PoorMansFeed;

/// <summary>Asks for the IPinfo API token. Returns the trimmed token, or null when cancelled.</summary>
public sealed partial class TokenDialog : Window
{
    public TokenDialog() => InitializeComponent();

    internal TokenDialog(TokenPromptReason reason) : this()
    {
        string title = reason == TokenPromptReason.Reset ? AppText.TokenResetTitle : AppText.TokenFirstTitle;
        Title = title;
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("BodyText")!.Text = BodyFor(reason);
        this.FindControl<TextBox>("TokenBox")!.TextChanged += (_, _) => UpdateSaveEnabled();
        UpdateSaveEnabled();
    }

    // A reset has already been explained, so it gets the body without the first sentence
    private static string BodyFor(TokenPromptReason reason) =>
        reason == TokenPromptReason.FirstRun ? AppText.TokenBody : AppText.TokenResetBody;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        this.FindControl<TextBox>("TokenBox")!.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void UpdateSaveEnabled()
    {
        string text = this.FindControl<TextBox>("TokenBox")!.Text ?? "";
        this.FindControl<Button>("SaveButton")!.IsEnabled = text.Trim().Length > 0;
    }

    private void ShowTokenChanged(object? sender, RoutedEventArgs e) =>
        this.FindControl<TextBox>("TokenBox")!.RevealPassword = this.FindControl<CheckBox>("ShowToken")!.IsChecked == true;

    private void SaveClicked(object? sender, RoutedEventArgs e)
    {
        string token = (this.FindControl<TextBox>("TokenBox")!.Text ?? "").Trim();
        if (token.Length > 0)
        {
            Close(token);
        }
    }

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(null);
}
