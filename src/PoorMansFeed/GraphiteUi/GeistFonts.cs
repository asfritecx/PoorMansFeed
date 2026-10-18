using Avalonia;
using Avalonia.Media.Fonts;

namespace GraphiteUi;

/// <summary>
/// Geist and Geist Mono (SIL OFL 1.1, GraphiteUi/Fonts/OFL.txt), embedded the way Avalonia.Fonts.Inter embeds Inter.
/// The files group by their typographic family name, so the collection holds two families: "Geist" (400, 500, 600,
/// 700) and "Geist Mono" (400, 500). XAML names them as fonts:Geist#Geist and fonts:Geist#Geist Mono. The asset path
/// is built from this assembly's name, so the kit needs no edit in another tool.
/// </summary>
public sealed class GeistFontCollection() : EmbeddedFontCollection(
    new Uri("fonts:Geist", UriKind.Absolute),
    new Uri($"avares://{typeof(GeistFontCollection).Assembly.GetName().Name}/GraphiteUi/Fonts", UriKind.Absolute));

public static class GeistFontsAppBuilderExtensions
{
    /// <summary>
    /// Registers the Geist collection. Without it, ContentControlThemeFontFamily falls through to the system font with
    /// no error, and a single-family reference such as the mono one finds no font at all.
    /// </summary>
    public static AppBuilder WithGeistFonts(this AppBuilder builder) =>
        builder.ConfigureFonts(fonts => fonts.AddFontCollection(new GeistFontCollection()));
}
