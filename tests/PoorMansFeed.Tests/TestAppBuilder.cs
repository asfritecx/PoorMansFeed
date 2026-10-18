using Avalonia;
using Avalonia.Headless;
using PoorMansFeed;
using PoorMansFeed.Tests;
using GraphiteUi;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace PoorMansFeed.Tests;

/// <summary>
/// Headless Avalonia setup for [AvaloniaFact] tests. It uses the app's own App, so the real theme and styles apply, and
/// registers Geist as Program.cs does: without it every fonts:Geist lookup fails.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions())
        .WithGeistFonts();
}
