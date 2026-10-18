using System.Net.Http;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PoorMansFeed.Core;
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
            IIpInfoService service = paths is null
                ? new UnavailableIpInfoService()
                : new IpInfoService(paths, http, new CoreOptions(), TimeProvider.System);
            var window = new MainWindow();
            var ui = new AvaloniaUiServices(window);
            string defaultExportFolder = paths?.DefaultExportFolder ?? Path.Combine(service.DataFolder, "exports");
            var googleFeeds = new GoogleCloudFeedService(service, http, new CoreOptions(), TimeProvider.System, defaultExportFolder);
            var azureFeeds = new AzureFeedService(service, http, new CoreOptions(), TimeProvider.System, paths, defaultExportFolder);
            var awsFeeds = new AwsFeedService(service, http, new CoreOptions(), TimeProvider.System, paths, defaultExportFolder);
            window.AttachViewModel(
                new MainViewModel(service, ui, paths?.DefaultExportFolder),
                new GoogleCloudFeedViewModel(googleFeeds, service, ui, paths?.DefaultExportFolder),
                new AzureFeedViewModel(azureFeeds, service, ui, TimeProvider.System, paths?.DefaultExportFolder),
                new AwsFeedViewModel(awsFeeds, service, ui, TimeProvider.System, paths?.DefaultExportFolder),
                initializeOnOpen: true);
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

    private void ShowSettingsClicked(object? sender, EventArgs e) => RunMainCommand(vm => vm.OpenSettingsCommand);

    private void ResetTokenClicked(object? sender, EventArgs e) => RunMainCommand(vm => vm.ResetTokenCommand);

    // The menu runs the same commands as the window buttons, and only when the window would allow them
    private void RunMainCommand(Func<MainViewModel, ICommand> pick)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.DataContext: MainViewModel viewModel })
        {
            ICommand command = pick(viewModel);
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
        }
    }

    // Timeout is infinite because the dataset download is long; the core applies its own per-request limits
    private static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
}
