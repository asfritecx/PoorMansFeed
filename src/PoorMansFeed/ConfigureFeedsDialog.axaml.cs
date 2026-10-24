using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PoorMansFeed;

/// <summary>Shown by a feed press while no feed type is ticked. Returns the button chosen; any other close is Close.</summary>
public sealed partial class ConfigureFeedsDialog : Window
{
    public ConfigureFeedsDialog() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OpenSettingsClicked(object? sender, RoutedEventArgs e) => Close(ConfigureFeedsChoice.OpenSettings);

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close(ConfigureFeedsChoice.Close);
}
