using Avalonia;
using GraphiteUi;

namespace PoorMansFeed;

internal sealed class Program
{
    // Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant code before
    // AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration; also used by the visual designer and the previewer, so keep it public and static.
    // WithGeistFonts is required: GraphiteUi/Theme.axaml points ContentControlThemeFontFamily at the embedded Geist, and
    // without it the app falls back to the system font with no error.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithGeistFonts()
            .LogToTrace();
}
