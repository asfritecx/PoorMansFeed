using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PoorMansFeed;

/// <summary>
/// Asks which large ranges to export. Every range starts unticked. Returns the networks ticked, which may be empty,
/// or null when cancelled.
/// </summary>
public sealed partial class LargeRangesDialog : Window
{
    private IReadOnlyList<LargeRangeChoice> _choices = [];

    public LargeRangesDialog() => InitializeComponent();

    internal LargeRangesDialog(LargeRangePrompt prompt) : this()
    {
        Title = prompt.Title;
        this.FindControl<TextBlock>("TitleText")!.Text = prompt.Title;
        this.FindControl<TextBlock>("MessageText")!.Text = prompt.Message;

        _choices = prompt.Choices;
        this.FindControl<ItemsControl>("RangeList")!.ItemsSource = _choices;
        foreach (LargeRangeChoice choice in _choices)
        {
            choice.PropertyChanged += ChoicePropertyChanged;
        }

        UpdateCount();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnClosed(EventArgs e)
    {
        foreach (LargeRangeChoice choice in _choices)
        {
            choice.PropertyChanged -= ChoicePropertyChanged;
        }

        base.OnClosed(e);
    }

    private void ChoicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LargeRangeChoice.IsIncluded))
        {
            UpdateCount();
        }
    }

    private void UpdateCount()
    {
        int ticked = _choices.Count(choice => choice.IsIncluded);
        this.FindControl<TextBlock>("CountText")!.Text = AppText.LargeRangesTicked(ticked, _choices.Count);
    }

    private void ExportClicked(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> included = _choices
            .Where(choice => choice.IsIncluded)
            .Select(choice => choice.Network)
            .ToList();
        Close(included);
    }

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(null);
}
