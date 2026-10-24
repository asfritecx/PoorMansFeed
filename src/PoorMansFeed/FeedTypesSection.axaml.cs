using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PoorMansFeed;

/// <summary>The Settings page's feed types and ModSecurity defaults, bound to the page's view model.</summary>
public sealed partial class FeedTypesSection : UserControl
{
    public FeedTypesSection()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
