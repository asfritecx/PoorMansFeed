using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace GraphiteUi;

public sealed partial class AboutDialog : Window
{
    public AboutDialog() => AvaloniaXamlLoader.Load(this);

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}
