using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed;

/// <summary>
/// The exceptions being edited: the entries, the add boxes and the one feedback line. Nothing is saved here; the
/// owner saves ToEntries(). <see cref="Load"/> takes a saved list again, and <see cref="HasChanges"/> says whether the
/// entries differ from it.
/// </summary>
public sealed class ExceptionsEditorModel : ObservableObject
{
    private string _networkText = "";
    private string _noteText = "";
    private string _feedbackText = "";
    private bool _isFeedbackError;
    private IReadOnlyList<(string Network, string Note)> _baseline = [];

    // Set while Load fills the rows, so their change notifications are raised once at the end rather than per row
    private bool _loading;

    public ExceptionsEditorModel(ExceptionList current)
    {
        Rows.CollectionChanged += RowsChanged;
        Load(current);
    }

    /// <summary>
    /// Replaces the rows with the saved list's and clears the boxes and the feedback line. The rows' change
    /// notifications are raised once, after every row is in; <see cref="Rows"/> still raises its own for the list.
    /// </summary>
    public void Load(ExceptionList current)
    {
        ArgumentNullException.ThrowIfNull(current);
        _loading = true;
        try
        {
            Rows.Clear();
            if (current.IsReadable)
            {
                foreach (ExceptionEntry entry in current.Entries)
                {
                    Rows.Add(new ExceptionRow(entry, Remove));
                }
            }
        }
        finally
        {
            _loading = false;
        }

        _baseline = Snapshot();
        NetworkText = "";
        NoteText = "";
        if (current.IsReadable)
        {
            ShowFeedback(string.Empty, isError: false);
        }
        else
        {
            ShowFeedback(AppText.ExceptionsUnreadableWarning, isError: true);
        }

        RaiseRowsChanged();
    }

    /// <summary>True when the entries differ from the list last loaded, in order, network and note.</summary>
    public bool HasChanges => !Snapshot().SequenceEqual(_baseline);

    public ObservableCollection<ExceptionRow> Rows { get; } = [];

    public string NetworkText { get => _networkText; set => SetProperty(ref _networkText, value ?? ""); }

    public string NoteText { get => _noteText; set => SetProperty(ref _noteText, value ?? ""); }

    public string FeedbackText { get => _feedbackText; private set => SetProperty(ref _feedbackText, value); }

    public bool IsFeedbackError { get => _isFeedbackError; private set => SetProperty(ref _isFeedbackError, value); }

    public bool HasFeedback => FeedbackText.Length > 0;

    public bool HasRows => Rows.Count > 0;

    public string CountText => AppText.ExceptionsCount(Rows.Count);

    /// <summary>
    /// Adds the network in the box with its note, then clears both boxes. False, with the reason in the feedback line,
    /// when the text isn't a network, the network is already listed or the list is full.
    /// </summary>
    public bool TryAdd()
    {
        if (Rows.Count >= ExceptionList.MaxEntries)
        {
            ShowFeedback(AppText.ExceptionsFull(ExceptionList.MaxEntries), isError: true);
            return false;
        }

        if (!ExceptionEntry.TryParseNetwork(NetworkText, out var network, out bool hostBitsCleared))
        {
            ShowFeedback(AppText.ExceptionInvalid, isError: true);
            return false;
        }

        if (Rows.Any(row => row.Entry.Network.Equals(network)))
        {
            ShowFeedback(AppText.ExceptionDuplicate(network.Text), isError: true);
            return false;
        }

        Rows.Add(new ExceptionRow(new ExceptionEntry(network, NoteText), Remove));
        ShowFeedback(hostBitsCleared ? AppText.ExceptionAddedMasked(network.Text) : AppText.ExceptionAdded(network.Text),
                     isError: false);
        NetworkText = "";
        NoteText = "";
        return true;
    }

    public void Remove(ExceptionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Rows.Remove(row))
        {
            ShowFeedback(AppText.ExceptionRemoved(row.NetworkText), isError: false);
        }
    }

    /// <summary>
    /// Before a save: a network typed but not added yet is added first, so it isn't lost. True when the box is blank or
    /// the network was added; false, with the reason in the feedback line, when it can't be added.
    /// </summary>
    public bool TryAddPending() => string.IsNullOrWhiteSpace(NetworkText) || TryAdd();

    public IReadOnlyList<ExceptionEntry> ToEntries() => Rows.Select(row => row.Entry).ToList();

    private List<(string Network, string Note)> Snapshot() => Rows.Select(row => (row.Entry.Network.Text, row.Entry.Note)).ToList();

    private void ShowFeedback(string text, bool isError)
    {
        IsFeedbackError = isError;
        FeedbackText = text;
        OnPropertyChanged(nameof(HasFeedback));
    }

    private void RowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_loading)
        {
            RaiseRowsChanged();
        }
    }

    private void RaiseRowsChanged()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasChanges));
    }
}

/// <summary>One line of the Settings page's exceptions list, already formatted, with its own Remove command.</summary>
public sealed class ExceptionRow
{
    public ExceptionRow(ExceptionEntry entry, Action<ExceptionRow> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public ExceptionEntry Entry { get; }

    public string NetworkText => Entry.Network.Text;

    public string NoteText => Entry.Note.Length == 0 ? AppText.Missing : Entry.Note;

    public string RemoveName => AppText.ExceptionRemoveName(NetworkText);

    public IRelayCommand RemoveCommand { get; }
}
