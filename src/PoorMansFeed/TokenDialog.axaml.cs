using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PoorMansFeed;

/// <summary>
/// Asks for an API secret (the IPinfo token or the AbuseIPDB key). Returns the trimmed text, or null when cancelled.
/// </summary>
public sealed partial class TokenDialog : Window
{
    public TokenDialog() => InitializeComponent();

    internal TokenDialog(TokenPromptReason reason) : this(SecretPrompt.ForIpInfo(reason)) { }

    // The prompt is the one source of the dialog's texts: the XAML names none of them
    internal TokenDialog(SecretPrompt prompt) : this()
    {
        Title = prompt.Title;
        this.FindControl<TextBlock>("TitleText")!.Text = prompt.Title;
        this.FindControl<TextBlock>("BodyText")!.Text = prompt.Body;
        var box = this.FindControl<TextBox>("TokenBox")!;
        box.PlaceholderText = prompt.BoxName;
        AutomationProperties.SetName(box, prompt.BoxName);
        this.FindControl<CheckBox>("ShowToken")!.Content = prompt.ShowText;
        this.FindControl<Button>("SaveButton")!.Content = prompt.SaveText;
        box.TextChanged += (_, _) => UpdateSaveEnabled();
        UpdateSaveEnabled();
    }

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
