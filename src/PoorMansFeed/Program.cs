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

    // Also used by the visual designer and the previewer, so keep it public and static. Without WithGeistFonts the
    // theme's Geist falls back to the system font with no error.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithGeistFonts()
            .LogToTrace();
}
