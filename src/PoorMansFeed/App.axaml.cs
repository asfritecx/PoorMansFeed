using System.Net.Http;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.GoogleCloud;
using GraphiteUi;

namespace PoorMansFeed;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composed by hand: one window, one view model. Without a data folder the app runs on UnavailableIpInfoService.
            var paths = AppPaths.ForCurrentUser();
            HttpClient http = CreateHttpClient();
            // The AbuseIPDB service keeps its download accounting in the real service's config store, so it is made only
            // over a real service
            IpInfoService? ipinfo = paths is null
                ? null
                : IpInfoService.ForCurrentUser(paths, http, new CoreOptions(), TimeProvider.System);
            IIpInfoService service = ipinfo ?? (IIpInfoService)new UnavailableIpInfoService();
            IAbuseIpDbService? abuse = paths is null || ipinfo is null
                ? null
                : AbuseIpDbService.ForCurrentUser(paths, ipinfo, new CoreOptions(), TimeProvider.System);
            var window = new MainWindow();
            var ui = new AvaloniaUiServices(window);
            string defaultExportFolder = paths?.DefaultExportFolder ?? Path.Combine(service.DataFolder, "exports");
            var googleFeeds = new GoogleCloudFeedService(service, http, new CoreOptions(), TimeProvider.System, defaultExportFolder);
            var azureFeeds = new AzureFeedService(service, http, new CoreOptions(), TimeProvider.System, paths, defaultExportFolder);
            var awsFeeds = new AwsFeedService(service, http, new CoreOptions(), TimeProvider.System, paths, defaultExportFolder);
            // One gate for the window: a dialog, a save or an update on one page holds the commands of the others. The
            // key prompt is built on it and shared, so Lookup and Settings never ask for the key at the same time.
            var gate = new WorkGate();
            AbuseKeyPrompt? abuseKeys = abuse is null ? null : new AbuseKeyPrompt(ui, abuse, gate);
            // A feed's result line says when the Settings page holds edits; the page is built after main, which it takes
            var unsaved = new LateUnsavedSettings();
            var main = new MainViewModel(service, ui, paths?.DefaultExportFolder, abuse, gate, abuseKeys, unsaved);
            var settings = new SettingsPageViewModel(service, ui, defaultExportFolder, gate, main.ResetTokenAsync, abuse, abuseKeys);
            unsaved.Source = settings;
            window.AttachViewModel(
                main,
                new GoogleCloudFeedViewModel(googleFeeds, service, ui, gate, paths?.DefaultExportFolder, unsaved),
                new AzureFeedViewModel(azureFeeds, service, ui, TimeProvider.System, gate, paths?.DefaultExportFolder, unsaved),
                new AwsFeedViewModel(awsFeeds, service, ui, TimeProvider.System, gate, paths?.DefaultExportFolder, unsaved),
                settings,
                initializeOnOpen: true,
                abuseIpDb: new AbuseIpDbViewModel(abuse, service, ui, gate, abuseKeys, TimeProvider.System,
                                                  paths?.DefaultExportFolder, unsaved));
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void ShowAboutClicked(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            await new AboutDialog().ShowDialog(owner);
        }
    }

    private void ShowSettingsClicked(object? sender, EventArgs e) => MainWindowOrNull()?.ShowSettingsPage();

    private void ResetTokenClicked(object? sender, EventArgs e) => MainWindowOrNull()?.ResetTokenFromMenu();

    private MainWindow? MainWindowOrNull() =>
        ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow as MainWindow : null;

    // Timeout is infinite because the dataset download is long; the core applies its own per-request limits
    private static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
}
