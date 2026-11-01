using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PoorMansFeed;

/// <summary>The dataset update's own window, shown beside the pages. Closing it hides it and the update goes on.</summary>
public sealed partial class DatasetUpdateWindow : Window
{
    private bool _closingWithOwner;

    public DatasetUpdateWindow() => InitializeComponent();

    /// <summary>Closes the window for good. A hidden window has no owner, so the main window's close doesn't reach it,
    /// and the app would keep running for a window nobody can see.</summary>
    internal void CloseWithOwner()
    {
        _closingWithOwner = true;
        Close();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void CloseClicked(object? sender, RoutedEventArgs e) => Hide();

    /// <summary>Whether a close only hides the window. Only its own close does: cancelling the owner's close, or the
    /// app's shutdown, would keep the main window open.</summary>
    internal static bool HidesOnClose(WindowCloseReason reason) => reason == WindowCloseReason.WindowClosing;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!_closingWithOwner && HidesOnClose(e.CloseReason))
        {
            e.Cancel = true;
            Hide();
        }
    }
}
