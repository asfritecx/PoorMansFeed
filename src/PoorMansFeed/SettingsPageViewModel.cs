using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed;

/// <summary>Raised after the settings were saved, so the pages that show them can follow.</summary>
public sealed class SettingsSavedEventArgs(bool ipv6Changed) : EventArgs
{
    public bool IPv6Changed { get; } = ipv6Changed;
}

/// <summary>
/// Every action waits on the shared gate, so nothing here starts while an update, a dialog or a save is out. The
/// AbuseIPDB key passes through <see cref="AbuseKeyPrompt"/> only and is never held here.
/// </summary>
public sealed class SettingsPageViewModel : ObservableObject, IDisposable, IUnsavedSettings
{
    private readonly IIpInfoService _service;
    private readonly IUiServices _ui;
    private readonly IWorkGate _gate;
    private readonly Func<Task<TokenResetOutcome>> _resetToken;
    private readonly IAbuseIpDbService? _abuse;
    private readonly AbuseKeyPrompt? _abuseKeys;
    private readonly string _defaultExportFolder;
    private bool _disposed;
    private bool _isWorking;

    // The last result of each section. A settings or exceptions result shows until the next edit.
    private string _settingsResult = "";
    private bool _isSettingsResultError;
    private string _exceptionsResult = "";
    private bool _isExceptionsResultError;
    private string _keysStatusText = "";

    internal SettingsPageViewModel(IIpInfoService service, IUiServices ui, string defaultExportFolder, IWorkGate gate,
        Func<Task<TokenResetOutcome>> resetToken, IAbuseIpDbService? abuse = null, AbuseKeyPrompt? abuseKeys = null,
        TimeProvider? time = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _resetToken = resetToken ?? throw new ArgumentNullException(nameof(resetToken));
        _abuse = abuse;
        if (abuseKeys is not null && !ReferenceEquals(abuseKeys.Gate, _gate))
        {
            throw new ArgumentException("The key prompt must hold the page's own gate.", nameof(abuseKeys));
        }

        _abuseKeys = abuseKeys;
        _defaultExportFolder = defaultExportFolder;

        Settings = new SettingsFormModel(service.Settings, defaultExportFolder, time);
        Exceptions = new ExceptionsEditorModel(service.Exceptions);

        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, CanSaveSettings);
        RevertSettingsCommand = new RelayCommand(RevertSettings, () => CanEdit() && Settings.HasChanges);
        ChooseFolderCommand = new AsyncRelayCommand(ChooseFolderAsync, CanWork);
        UseDefaultFolderCommand = new RelayCommand(() => Settings.UseDefaultFolder(), CanEdit);
        AddExceptionCommand = new RelayCommand(() => Exceptions.TryAdd(), CanEdit);
        SaveExceptionsCommand = new AsyncRelayCommand(SaveExceptionsAsync, CanSaveExceptions);
        RevertExceptionsCommand = new RelayCommand(RevertExceptions, () => CanEdit() && HasUnsavedExceptions);
        ResetTokenCommand = new AsyncRelayCommand(ResetTokenAsync, CanWork);
        SetAbuseKeyCommand = new AsyncRelayCommand(SetAbuseKeyAsync, () => CanWork() && _abuseKeys is { IsOpen: false });
        RemoveAbuseKeyCommand = new AsyncRelayCommand(RemoveAbuseKeyAsync, CanRemoveAbuseKey);

        _gate.Changed += GateChanged;
        if (_abuseKeys is not null)
        {
            _abuseKeys.IsOpenChanged += GateChanged;
        }

        Settings.PropertyChanged += SettingsChanged;
        Exceptions.PropertyChanged += ExceptionsChanged;
    }

    public SettingsFormModel Settings { get; }

    public ExceptionsEditorModel Exceptions { get; }

    /// <summary>A save, the folder picker, a confirmation or a key action is out.</summary>
    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (SetProperty(ref _isWorking, value))
            {
                RefreshCommands();
            }
        }
    }

    /// <summary>
    /// The inputs can be typed in: the app has started, so start-up can no longer replace what is typed, and no
    /// save, picker, confirmation or key action of this page is out.
    /// </summary>
    public bool CanEditInputs => CanEdit();

    /// <summary>
    /// The app has started, so the page holds the saved settings and exceptions. Until then the page shows one line in
    /// place of its cards, because what it holds is only the defaults start-up will replace.
    /// </summary>
    public bool IsStarted => _gate.IsStarted;

    /// <summary>The settings or the exceptions have edits that aren't saved. The Settings nav item shows it on every page.</summary>
    public bool HasUnsavedChanges => Settings.HasChanges || HasUnsavedExceptions;

    /// <summary>
    /// A network typed but not added counts as unsaved; a note typed alone doesn't, because Save adds nothing for it.
    /// </summary>
    private bool HasUnsavedExceptions => Exceptions.HasChanges || !string.IsNullOrWhiteSpace(Exceptions.NetworkText);

    public IAsyncRelayCommand SaveSettingsCommand { get; }
    public IRelayCommand RevertSettingsCommand { get; }
    public IAsyncRelayCommand ChooseFolderCommand { get; }
    public IRelayCommand UseDefaultFolderCommand { get; }
    public IRelayCommand AddExceptionCommand { get; }
    public IAsyncRelayCommand SaveExceptionsCommand { get; }
    public IRelayCommand RevertExceptionsCommand { get; }
    public IAsyncRelayCommand ResetTokenCommand { get; }
    public IAsyncRelayCommand SetAbuseKeyCommand { get; }
    public IAsyncRelayCommand RemoveAbuseKeyCommand { get; }

    /// <summary>What is wrong with the edits, else the last save's line until the next edit, else whether edits are unsaved.</summary>
    public string SettingsStatusText =>
        !Settings.IsValid ? Settings.ValidationText
        : _settingsResult.Length > 0 ? _settingsResult
        : Settings.HasChanges ? AppText.ChangesNotSaved
        : "";

    public bool HasSettingsStatus => SettingsStatusText.Length > 0;

    public bool IsSettingsStatusError => !Settings.IsValid || (_settingsResult.Length > 0 && _isSettingsResultError);

    /// <summary>The last save's line until the next edit, else whether edits are unsaved.</summary>
    public string ExceptionsStatusText =>
        _exceptionsResult.Length > 0 ? _exceptionsResult
        : HasUnsavedExceptions ? AppText.ChangesNotSaved
        : "";

    public bool HasExceptionsStatus => ExceptionsStatusText.Length > 0;

    public bool IsExceptionsStatusError => _exceptionsResult.Length > 0 && _isExceptionsResultError;

    /// <summary>The result of the last key action.</summary>
    public string KeysStatusText
    {
        get => _keysStatusText;
        private set
        {
            if (SetProperty(ref _keysStatusText, value))
            {
                OnPropertyChanged(nameof(HasKeysStatus));
            }
        }
    }

    public bool HasKeysStatus => KeysStatusText.Length > 0;

    public string TokenStatusText => _service.HasToken ? AppText.TokenSavedStatus : AppText.NoTokenStatus;

    public string AbuseKeyStatusText =>
        _abuse is null ? AppText.AbuseKeysUnavailable
        : _abuse.HasKey ? AppText.AbuseKeySavedStatus
        : AppText.NoAbuseKeyStatus;

    /// <summary>False when the app has no AbuseIPDB service (no data folder), so the key buttons can't be used.</summary>
    public bool CanUseAbuseKeys => _abuse is not null;

    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

    /// <summary>Takes the service's settings and exceptions again, as after a start-up, and clears the three lines.</summary>
    public void Reload()
    {
        if (_disposed)
        {
            return;
        }

        Settings.Load(_service.Settings);
        Exceptions.Load(_service.Exceptions);
        SetSettingsResult("", isError: false);
        SetExceptionsResult("", isError: false);
        KeysStatusText = "";
        RefreshKeyTexts();
        RefreshCommands();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /// <summary>
    /// False when the user declines or the confirmation can't be shown, so the edits stay. The gate is held while it
    /// asks, as for every other confirmation.
    /// </summary>
    internal async Task<bool> ConfirmDiscardAsync()
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        using IDisposable hold = _gate.Hold();
        try
        {
            return await _ui.ConfirmAsync(AppText.CloseUnsavedTitle, AppText.CloseUnsavedBody, AppText.CloseUnsavedButton);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool CanEdit() => !_disposed && !IsWorking && _gate.IsStarted;

    private bool CanWork() => CanEdit() && _gate.CanStart;

    private bool CanSaveSettings() => CanWork() && Settings.IsValid && Settings.HasChanges;

    private bool CanSaveExceptions() => CanWork() && HasUnsavedExceptions;

    private bool CanRemoveAbuseKey() => CanWork() && _abuse is { HasKey: true } && _abuseKeys is not { IsOpen: true };

    private async Task SaveSettingsAsync()
    {
        if (!CanSaveSettings())
        {
            return;
        }

        using IDisposable hold = _gate.Hold();
        IsWorking = true;
        try
        {
            UserSettings edited = Settings.ToSettings();
            bool ipv6Changed = edited.IPv6Enabled != _service.Settings.IPv6Enabled;
            string folderBefore = ExportFolders.Resolve(_service.Settings, _defaultExportFolder);
            bool saved;
            try
            {
                saved = await _service.SaveSettingsAsync(edited);
            }
            catch (Exception)
            {
                saved = false;
            }

            if (_disposed)
            {
                return;
            }

            if (saved)
            {
                // The store normalises what it saves, so the form shows what was saved
                Settings.Load(_service.Settings);
                string folderAfter = ExportFolders.Resolve(_service.Settings, _defaultExportFolder);
                SetSettingsResult(SameFolder(folderBefore, folderAfter) ? AppText.SettingsSaved : AppText.SettingsSavedFolderChanged,
                    isError: false);
                OnPropertyChanged(nameof(HasUnsavedChanges));
                SettingsSaved?.Invoke(this, new SettingsSavedEventArgs(ipv6Changed));
            }
            else
            {
                SetSettingsResult(AppText.SettingsSaveFailed, isError: true);
            }
        }
        finally
        {
            IsWorking = false;
        }
    }

    // Compared as full paths without a trailing separator. The save has committed by now, so a path that can't be made
    // full falls back to the plain text rather than throwing past the saved event.
    private static bool SameFolder(string before, string after)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(before)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(after)), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                   or NotSupportedException or System.Security.SecurityException)
        {
            return string.Equals(before, after, StringComparison.Ordinal);
        }
    }

    private void RevertSettings()
    {
        Settings.Load(_service.Settings);
        SetSettingsResult("", isError: false);
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private async Task ChooseFolderAsync()
    {
        if (!CanWork())
        {
            return;
        }

        using IDisposable hold = _gate.Hold();
        IsWorking = true;
        try
        {
            string? path = await _ui.ChooseFolderAsync(Settings.ExportFolder);
            if (!_disposed && path is not null)
            {
                Settings.ExportFolder = path;
            }
        }
        catch (Exception)
        {
            SetSettingsResult(AppText.FolderPickerFailed, isError: true);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private async Task SaveExceptionsAsync()
    {
        if (!CanSaveExceptions())
        {
            return;
        }

        // A network still in the box is added first; one that can't be added says why in the feedback line
        if (!Exceptions.TryAddPending())
        {
            return;
        }

        using IDisposable hold = _gate.Hold();
        IsWorking = true;
        try
        {
            IReadOnlyList<ExceptionEntry> entries = Exceptions.ToEntries();
            ExceptionsSaveOutcome outcome;
            try
            {
                outcome = await _service.SaveExceptionsAsync(entries);
            }
            catch (Exception)
            {
                outcome = ExceptionsSaveOutcome.Failed;
            }

            if (_disposed)
            {
                return;
            }

            if (outcome == ExceptionsSaveOutcome.Saved)
            {
                Exceptions.Load(_service.Exceptions);
                SetExceptionsResult(AppText.ExceptionsSaved(entries.Count), isError: false);
                OnPropertyChanged(nameof(HasUnsavedChanges));
            }
            else
            {
                SetExceptionsResult(AppText.ExceptionsSaveFailed, isError: true);
            }
        }
        finally
        {
            IsWorking = false;
        }
    }

    private void RevertExceptions()
    {
        Exceptions.Load(_service.Exceptions);
        SetExceptionsResult("", isError: false);
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    // The main view model holds the gate itself for the whole flow, from the confirmation to the save, so no hold is
    // taken here
    private async Task ResetTokenAsync()
    {
        if (!CanWork())
        {
            return;
        }

        IsWorking = true;
        TokenResetOutcome outcome;
        try
        {
            outcome = await _resetToken();
        }
        catch (Exception)
        {
            outcome = TokenResetOutcome.DialogFailed;
        }
        finally
        {
            IsWorking = false;
        }

        if (_disposed)
        {
            return;
        }

        KeysStatusText = outcome switch
        {
            TokenResetOutcome.Saved => AppText.TokenSaved,
            TokenResetOutcome.Declined => "",
            TokenResetOutcome.Busy => AppText.KeysBusy,
            TokenResetOutcome.ClearFailed => AppText.TokenClearFailed,
            TokenResetOutcome.NoNewToken => AppText.TokenRemovedNoNew,
            TokenResetOutcome.SaveFailed => AppText.TokenSaveFailed,
            _ => AppText.Generic,
        };
        RefreshKeyTexts();
    }

    // The prompt holds the gate itself, so no hold is taken here
    private async Task SetAbuseKeyAsync()
    {
        if (!CanWork() || _abuseKeys is not { } keys)
        {
            return;
        }

        IsWorking = true;
        AbuseKeyOutcome outcome;
        try
        {
            outcome = await keys.AskAndSaveAsync(AbuseKeyAsk.Replace);
        }
        finally
        {
            IsWorking = false;
        }

        if (_disposed)
        {
            return;
        }

        KeysStatusText = outcome switch
        {
            AbuseKeyOutcome.Saved => AppText.AbuseKeySaved,
            AbuseKeyOutcome.Cancelled => "",
            AbuseKeyOutcome.Busy => AppText.KeysBusy,
            AbuseKeyOutcome.NotAKey => AppText.AbuseKeyNotAKey,
            AbuseKeyOutcome.SaveFailed => AppText.AbuseKeySaveFailed,
            _ => AppText.Generic,
        };
        RefreshKeyTexts();
    }

    private async Task RemoveAbuseKeyAsync()
    {
        if (!CanRemoveAbuseKey() || _abuse is not { } abuse)
        {
            return;
        }

        using IDisposable hold = _gate.Hold();
        IsWorking = true;
        try
        {
            bool confirmed;
            try
            {
                confirmed = await _ui.ConfirmAsync(AppText.AbuseKeyRemoveTitle, AppText.AbuseKeyRemoveBody, AppText.AbuseKeyRemoveButton);
            }
            catch (Exception)
            {
                KeysStatusText = AppText.Generic;
                return;
            }

            if (!confirmed || _disposed)
            {
                return;
            }

            bool cleared;
            try
            {
                cleared = await abuse.ClearKeyAsync();
            }
            catch (Exception)
            {
                cleared = false;
            }

            if (!_disposed)
            {
                KeysStatusText = cleared ? AppText.AbuseKeyRemoved : AppText.AbuseKeyRemoveFailed;
            }
        }
        finally
        {
            IsWorking = false;
            // A failed removal keeps the key, so whether one is saved is read again rather than assumed
            RefreshKeyTexts();
        }
    }

    private void SetSettingsResult(string text, bool isError)
    {
        _settingsResult = text;
        _isSettingsResultError = isError;
        RaiseSettingsStatus();
    }

    private void SetExceptionsResult(string text, bool isError)
    {
        _exceptionsResult = text;
        _isExceptionsResultError = isError;
        RaiseExceptionsStatus();
    }

    // An edit drops the last result, so the line then says the edits are unsaved
    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        _settingsResult = "";
        _isSettingsResultError = false;
        RaiseSettingsStatus();
        OnPropertyChanged(nameof(HasUnsavedChanges));
        SaveSettingsCommand.NotifyCanExecuteChanged();
        RevertSettingsCommand.NotifyCanExecuteChanged();
    }

    private void ExceptionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        _exceptionsResult = "";
        _isExceptionsResultError = false;
        RaiseExceptionsStatus();
        OnPropertyChanged(nameof(HasUnsavedChanges));
        SaveExceptionsCommand.NotifyCanExecuteChanged();
        RevertExceptionsCommand.NotifyCanExecuteChanged();
    }

    private void RaiseSettingsStatus()
    {
        OnPropertyChanged(nameof(SettingsStatusText));
        OnPropertyChanged(nameof(HasSettingsStatus));
        OnPropertyChanged(nameof(IsSettingsStatusError));
    }

    private void RaiseExceptionsStatus()
    {
        OnPropertyChanged(nameof(ExceptionsStatusText));
        OnPropertyChanged(nameof(HasExceptionsStatus));
        OnPropertyChanged(nameof(IsExceptionsStatusError));
    }

    /// <summary>Raises the key lines and the expiry note again, so they read as of now. A page show calls it.</summary>
    internal void RefreshKeyTexts()
    {
        OnPropertyChanged(nameof(TokenStatusText));
        OnPropertyChanged(nameof(AbuseKeyStatusText));
        Settings.RefreshExpiry();
        RefreshCommands();
    }

    // A token or key saved outside the page (a first-run or update prompt, Check AbuseIPDB's prompt) ends with a gate
    // flip or the prompt closing, so the key texts follow it here.
    private void GateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsStarted));
        RefreshKeyTexts();
    }

    // The inputs' switch follows the same things as the commands: IsWorking, the gate and disposal
    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEditInputs));
        SaveSettingsCommand.NotifyCanExecuteChanged();
        RevertSettingsCommand.NotifyCanExecuteChanged();
        ChooseFolderCommand.NotifyCanExecuteChanged();
        UseDefaultFolderCommand.NotifyCanExecuteChanged();
        AddExceptionCommand.NotifyCanExecuteChanged();
        SaveExceptionsCommand.NotifyCanExecuteChanged();
        RevertExceptionsCommand.NotifyCanExecuteChanged();
        ResetTokenCommand.NotifyCanExecuteChanged();
        SetAbuseKeyCommand.NotifyCanExecuteChanged();
        RemoveAbuseKeyCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Changed -= GateChanged;
        if (_abuseKeys is not null)
        {
            _abuseKeys.IsOpenChanged -= GateChanged;
        }

        Settings.PropertyChanged -= SettingsChanged;
        Exceptions.PropertyChanged -= ExceptionsChanged;
        RefreshCommands();
    }
}
