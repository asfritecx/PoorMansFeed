using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PoorMansFeed;

/// <summary>The checked ranges of a cloud tool page. Its DataContext is the page's <see cref="CloudFeedPageViewModel"/>.</summary>
public sealed partial class CloudRangesPanel : UserControl
{
    public CloudRangesPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
