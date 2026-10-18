using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PoorMansFeed.Tests;

/// <summary>
/// The kit's Graphite look rests on resource lookups that fail silently: an accent override keyed in the wrong theme
/// dictionary, a font that falls back to the system one, a Fluent size that is not overridden. Each test pins one.
/// </summary>
public sealed class GraphiteThemeTests
{
    [AvaloniaTheory]
    [InlineData("Light", "#1767D0", "#FFFFFF")]
    [InlineData("Dark", "#7AB4FF", "#0A0B0D")]
    public void TheAccentButtonUsesGraphitesBlueAndItsOnAccentText(string variant, string fill, string text)
    {
        var button = new Button { Content = "Update dataset", Classes = { "accent" } };
        WithWindow(variant, button, () =>
        {
            // Fluent puts the accent colours on the template's presenter, not on the button
            ContentPresenter presenter = Presenter(button);
            Assert.Equal(Color.Parse(fill), Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
            Assert.Equal(Color.Parse(text), Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Foreground).Color);
        });
    }

    [AvaloniaTheory]
    [InlineData("Light", "#FFFFFF", "#F3F4F6")]
    [InlineData("Dark", "#1C2027", "#111317")]
    public void ACardSitsOnThePageSurfaceWithAShadow(string variant, string card, string page)
    {
        var border = new Border { Classes = { "card" }, Height = 40 };
        WithWindow(variant, border, () =>
        {
            Assert.Equal(Color.Parse(card), Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
            var window = Assert.IsType<Window>(TopLevel.GetTopLevel(border));
            Assert.Equal(Color.Parse(page), Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
            Assert.True(border.BoxShadow.Count > 0);
        });
    }

    [AvaloniaFact]
    public void TextIsSetInGeist()
    {
        var label = new TextBlock { Text = "Dataset" };
        WithWindow("Light", label, () => Assert.Equal("Geist", label.FontFamily.Name));
    }

    [AvaloniaFact]
    public void MonoTextIsSetInGeistMonoInEveryKindOfTextBlockAndTheTextBox()
    {
        // SelectableTextBlock derives from TextBlock, and a TextBlock.mono selector does not reach it
        var plain = new TextBlock { Text = "8.8.8.0/24", Classes = { "mono" } };
        var selectable = new SelectableTextBlock { Text = "AS15169", Classes = { "mono" } };
        var box = new TextBox { Text = "2001:4860::/32", Classes = { "mono" } };
        WithWindow("Light", new StackPanel { Children = { plain, selectable, box } }, () =>
        {
            Assert.Equal("Geist Mono", plain.FontFamily.Name);
            Assert.Equal("Geist Mono", selectable.FontFamily.Name);
            Assert.Equal("Geist Mono", box.FontFamily.Name);
        });
    }

    [AvaloniaFact]
    public void GeistAndGeistMonoResolveToTheEmbeddedFonts()
    {
        // Without WithGeistFonts these lookups fall back to the system font, which still "succeeds"
        AssertResolves("fonts:Geist#Geist", FontWeight.Normal, "Geist");
        AssertResolves("fonts:Geist#Geist", FontWeight.Medium, "Geist");
        AssertResolves("fonts:Geist#Geist", FontWeight.SemiBold, "Geist");
        AssertResolves("fonts:Geist#Geist Mono", FontWeight.Normal, "Geist Mono");
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ADisabledSubtleButtonHasNoFill(string variant)
    {
        var button = new Button { Content = "?", Classes = { "subtle" }, IsEnabled = false };
        WithWindow(variant, button, () =>
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(Presenter(button).Background).Color));
    }

    [AvaloniaFact]
    public void ButtonsTextBoxesAndComboBoxesAre36High()
    {
        var button = new Button { Content = "Open folder" };
        var box = new TextBox { Text = "AS15169" };
        var combo = new ComboBox { ItemsSource = new[] { "europe-west2" }, SelectedIndex = 0 };
        WithWindow("Light", new StackPanel { Spacing = 8, Children = { button, box, combo } }, () =>
        {
            Assert.Equal(36, button.Bounds.Height);
            Assert.Equal(36, box.Bounds.Height);
            Assert.Equal(36, combo.Bounds.Height);
        });
    }

    [AvaloniaTheory]
    [InlineData("Light", "#0F16191E")]
    [InlineData("Dark", "#1C2027")]
    public void TheSelectedNavItemIsSemiBoldOnTheNavFillWithItsIndicator(string variant, string fill)
    {
        var selected = new ListBoxItem { Content = "IPInfo Lookup" };
        var other = new ListBoxItem { Content = "Azure Lookup" };
        var nav = new ListBox { Classes = { "nav" }, SelectionMode = SelectionMode.AlwaysSelected, Items = { selected, other } };
        WithWindow(variant, nav, () =>
        {
            Assert.Same(selected, nav.SelectedItem);
            Assert.Equal(40, selected.Bounds.Height);
            Assert.Equal(40, other.Bounds.Height);
            Assert.Equal(Color.Parse(fill), Assert.IsAssignableFrom<ISolidColorBrush>(selected.Background).Color);
            Assert.Equal(FontWeight.SemiBold, selected.FontWeight);
            Assert.True(Indicator(selected).IsVisible);
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(other.Background).Color);
            Assert.Equal(FontWeight.Normal, other.FontWeight);
            Assert.False(Indicator(other).IsVisible);
        });
    }

    [AvaloniaTheory]
    [InlineData("Light", "#E9ECF0", "#F3F4F6")]
    [InlineData("Dark", "#0A0B0D", "#111317")]
    public void TheAppBarAndNavPaneSitOnTheWindowSurfaceAndThePageOnTheContentLayer(string variant, string window, string solid)
    {
        var appBar = new Border { Name = "AppBar" };
        var navPane = new Grid { Name = "NavPane" };
        var content = new Border { Name = "ContentLayer", Height = 40 };
        var shell = new Grid { Name = "Shell", Children = { appBar, navPane, content } };
        WithWindow(variant, shell, () =>
        {
            Assert.Equal(Color.Parse(window), Assert.IsAssignableFrom<ISolidColorBrush>(shell.Background).Color);
            Assert.Equal(48, appBar.Bounds.Height);
            Assert.Equal(248, navPane.Bounds.Width);
            Assert.Equal(Color.Parse(solid), Assert.IsAssignableFrom<ISolidColorBrush>(content.Background).Color);
            Assert.Equal(new Thickness(1, 1, 0, 0), content.BorderThickness);
            Assert.Equal(new CornerRadius(8, 0, 0, 0), content.CornerRadius);
        });
    }

    private static ContentPresenter Presenter(Button button) => button.GetVisualDescendants()
        .OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

    private static Border Indicator(ListBoxItem item) => item.GetVisualDescendants()
        .OfType<Border>().Single(b => b.Name == "PART_Indicator");

    private static void AssertResolves(string family, FontWeight weight, string expectedFamily)
    {
        Assert.True(FontManager.Current.TryGetGlyphTypeface(
            new Typeface(new FontFamily(family), FontStyle.Normal, weight), out GlyphTypeface? typeface));
        // A regular-weight file names its family only in name ID 1; the other weights add the shared name as ID 16
        string resolved = typeface!.TypographicFamilyName is { Length: > 0 } typographic ? typographic : typeface.FamilyName;
        Assert.Equal(expectedFamily, resolved);
        Assert.Equal(weight, typeface.Weight);
    }

    private static void WithWindow(string variant, Control content, Action test)
    {
        var window = new Window
        {
            RequestedThemeVariant = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light,
            Content = new StackPanel { Margin = new Thickness(24), Children = { content } },
        };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            test();
        }
        finally
        {
            window.Close();
        }
    }
}
