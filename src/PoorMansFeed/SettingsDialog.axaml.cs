using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PoorMansFeed.Core;

namespace PoorMansFeed;

/// <summary>Edits the settings. Returns the edited record, or null when cancelled.</summary>
public sealed partial class SettingsDialog : Window
{
    private SettingsDialogModel? _model;

    public SettingsDialog() => InitializeComponent();

    internal SettingsDialog(UserSettings current, string defaultExportFolder) : this()
    {
        _model = new SettingsDialogModel(current, defaultExportFolder);
        DataContext = _model;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void ChooseFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        // Start the picker in the folder currently shown, when that folder still exists
        IStorageFolder? start = null;
        if (Directory.Exists(_model.ExportFolder)
            && Uri.TryCreate(Path.GetFullPath(_model.ExportFolder), UriKind.Absolute, out Uri? folderUri))
        {
            start = await StorageProvider.TryGetFolderFromPathAsync(folderUri);
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = AppText.ChooseFolderTitle,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        // A cancelled picker gives no folders, and a folder without a local path cannot be used: both change nothing
        string? path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            _model.ExportFolder = path;
        }
    }

    private void UseDefaultClicked(object? sender, RoutedEventArgs e) => _model?.UseDefaultFolder();

    private void SaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_model is { IsValid: true })
        {
            Close(_model.ToSettings());
        }
    }

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(null);
}
