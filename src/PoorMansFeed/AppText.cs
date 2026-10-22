using System.Globalization;
using System.Numerics;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using GraphiteUi;

namespace PoorMansFeed;

/// <summary>
/// Every user-visible string of the main window, its view model and the dialogs (DESIGN section 4). Non-ASCII is
/// written as escapes so the source stays ASCII. Numbers use the invariant culture.
/// </summary>
public static class AppText
{
    private const string Ellipsis = "\u2026";

    // A middle dot between parallel facts; no-break spaces keep it from wrapping away from its words
    public const string Joiner = "\u00A0\u00B7\u00A0";

    // A spaced em dash between a status and its detail
    public const string Dash = " \u2014 ";

    // Shown in a column or part that has no value
    public const string Missing = "\u2014";

    public const string Attribution = "IP address data powered by IPinfo (ipinfo.io)" + Joiner + "CC BY-SA 4.0";

    public const string Cancel = "Cancel";
    public const string Close = "Close";
    public const string Save = "Save";
    public const string Export = "Export";

    // App bar and heading
    public const string Title = "IPInfo Lookup";
    public const string Caption = "Search the IPinfo Lite dataset by ASN or IP address";
    public const string UpdateButton = "Update dataset";
    public const string UpdateTooltip = "Download the latest dataset";

    // Section headers
    public const string DatasetHeader = "Dataset";
    public const string DatasetCaption = "The local copy of the IPinfo Lite dataset";
    public const string ProgressName = "Dataset update progress";
    public const string ResultsHeader = "Results";

    // Search card: the placeholder and the accessible name say what to type
    public const string SearchBoxName = "ASN or IP address";
    public const string SearchPlaceholder = "AS15169, 15169 or 8.8.8.8";
    public const string SearchButton = "Search";

    // Pill and dataset card: the pill and the card title share their wording
    public const string Starting = "Starting" + Ellipsis;
    // The pill while a close waits for a start-up step to return
    public const string ClosingAfterStartUp = "Closing after start-up" + Ellipsis;
    public const string NotStarted = "Not started";
    public const string CheckingForUpdates = "Checking for updates" + Ellipsis;
    public const string NoDataset = "No dataset";
    public const string UpToDate = "Dataset up to date";
    public const string UpdateAvailable = "Update available";
    public const string CheckFailed = "Update check failed";
    public const string Updating = "Updating" + Ellipsis;
    public const string CheckingDetail = "You can search while this runs.";
    public const string UpdateAvailableDetail = "A newer dataset is available. Choose Update dataset to download it.";
    public const string UpdateAvailableIPv6Detail = "Update the dataset to add IPv6 networks.";
    public const string IPv4Only = "IPv4 only";
    public const string AlreadyCurrentDetail = "No newer dataset is available.";
    public const string NoTokenCheck = "No API token could be read. For updates, choose Reset token in Settings.";
    public const string NoTokenFirstRun = "An API token is needed to download the dataset. Choose Update dataset to enter one.";
    public const string NoDatasetFacts = "No dataset has been downloaded yet";

    // Update phases
    public const string DownloadingTitle = "Downloading the dataset" + Ellipsis;
    public const string ImportingTitle = "Importing the dataset" + Ellipsis;
    public const string IndexingTitle = "Building the search index" + Ellipsis;
    public const string IndexingDetail = "This can take a minute.";
    public const string FinishingTitle = "Finishing" + Ellipsis;

    // Update outcomes
    public const string UpdatedTitle = "Dataset updated";
    public const string UpdateCancelledTitle = "Update cancelled";
    public const string UpdateFailedTitle = "Update failed";
    public const string StillInUse = "The existing dataset is still in use.";
    public const string NoDatasetDownloaded = "No dataset has been downloaded yet.";

    // Failure details, for a check or an update
    public const string TokenRejected = "The API token wasn't accepted. Choose Reset token in Settings.";
    public const string CheckOffline = "The update check couldn't be completed. Check the network connection, then choose Update dataset.";
    public const string UpdateOffline = "The dataset couldn't be downloaded. Check the network connection, then try again.";
    public const string ChecksumMismatch = "The download couldn't be verified, so it was discarded. Please try again.";
    public const string DiskFull = "The dataset couldn't be downloaded because the disk is nearly full. Free some space, then try again.";
    public const string CorruptData = "The downloaded dataset couldn't be read, so it was discarded. Please try again.";
    public const string StorageError = "The dataset couldn't be saved. Please try again or contact the Infra Team.";
    public const string ServiceError = "The update couldn't be completed. Please try again or contact the Infra Team.";
    public const string Generic = "The operation couldn't be completed. Please try again or contact the Infra Team.";

    // Start-up. The product name is a constant, so these are constants too.
    public const string PrivilegedTitle = "Administrator privileges";
    public const string PrivilegedBody = AppInfo.Name + " can't be run as an administrator. Please relaunch the application from a standard account.";
    public const string AlreadyOpenTitle = "Already open";
    public const string AlreadyOpenBody = AppInfo.Name + " is already open on this computer. Please use the window that is already open.";
    public const string StorageUnavailable = AppInfo.Name + " couldn't start. Its data folder couldn't be created. Please contact the Infra Team for assistance.";
    public const string DatabaseNewer = AppInfo.Name + " couldn't start. The dataset on this computer was created by a newer version. Please install the latest version.";

    // The start-up stop: a title (a fragment) and a body for every failure that keeps the app from starting
    public const string StorageUnavailableTitle = "Data folder unavailable";
    public const string DatabaseNewerTitle = "Dataset from a newer version";
    public const string ConfigNewerTitle = "Settings file from a newer version";
    public const string ConfigUnreadableTitle = "Settings file can't be read";
    public const string ConfigKeyMismatchTitle = "Settings file can't be opened";
    public const string ConfigLockedTitle = "Settings file in use";
    public const string ConfigStorageErrorTitle = "Settings file can't be written";
    public const string ConfigKeyUnavailableTitle = "Settings key can't be read";
    public const string ConfigKeyStoreMissingTitle = "No key store on this system";
    public const string ConfigMissingTitle = "Settings file missing";
    public const string ExceptionsImportUnreadableTitle = "Old exceptions file can't be read";

    public const string ConfigNewer = AppInfo.Name + " couldn't start. Its settings file, " + AppPaths.ConfigFileName +
        ", was made by a newer version. Please install the latest version, or start fresh with the default settings.";
    public const string ConfigUnreadable = AppInfo.Name + " couldn't start. Its settings file, " + AppPaths.ConfigFileName +
        ", is damaged or isn't a " + AppInfo.Name + " file. Start fresh to make a new one with the default settings.";
    public const string ConfigKeyMismatch = AppInfo.Name + " couldn't start. Its settings file, " + AppPaths.ConfigFileName +
        ", can't be opened with the key saved on this computer, or it was changed outside the application. Start fresh to make a new one with the default settings.";
    public const string ConfigLocked = AppInfo.Name + " couldn't start. Its settings file, " + AppPaths.ConfigFileName +
        ", is in use by another program. Close that program, then relaunch the application.";
    public const string ConfigStorageError = AppInfo.Name + " couldn't start. Its settings file, " + AppPaths.ConfigFileName +
        ", couldn't be opened or written. Check that the app data folder isn't read-only, then relaunch the application. If it still can't start, please contact the Infra Team for assistance.";
    public const string ConfigKeyUnavailable = AppInfo.Name + " couldn't start. The key to its settings file couldn't be read from this computer's key store, so nothing was changed. If you were asked for access, allow it, then relaunch the application.";
    public const string ConfigKeyStoreMissing = AppInfo.Name + " couldn't start. This system has no key store for the key to its settings file, so nothing was saved. " +
        AppInfo.Name + " runs on Windows and macOS.";
    public const string ConfigMissing = AppInfo.Name + " couldn't start. Its settings file, " + AppPaths.ConfigFileName +
        ", is missing or empty, though " + AppInfo.Name + " has run on this computer before. If you have a copy, put it back in the app data folder, then choose Start fresh to open it. Otherwise Start fresh makes a new one with the default settings and no exceptions. Any " +
        "settings.json" + AppPaths.MigratedSuffix + " and " + "exceptions.json" + AppPaths.MigratedSuffix + " files there are kept.";
    public const string ExceptionsImportUnreadable = AppInfo.Name + " couldn't start. The exceptions file from an earlier version, exceptions.json, couldn't be read, so it wasn't moved into the new settings file. Choose Start without exceptions to go on with an empty list; the file is then kept as " +
        AppPaths.UnreadableExceptionsFileName + ", so the networks it held can be copied back by hand. Or fix exceptions.json, then close " +
        AppInfo.Name + " and open it again.";

    /// <summary>The stop's title. Only the failures that stop the app have one.</summary>
    public static string StopTitle(StartupFailure failure) => failure switch
    {
        StartupFailure.StorageUnavailable => StorageUnavailableTitle,
        StartupFailure.DatabaseNewer => DatabaseNewerTitle,
        StartupFailure.ConfigNewer => ConfigNewerTitle,
        StartupFailure.ConfigUnreadable => ConfigUnreadableTitle,
        StartupFailure.ConfigKeyMismatch => ConfigKeyMismatchTitle,
        StartupFailure.ConfigLocked => ConfigLockedTitle,
        StartupFailure.ConfigStorageError => ConfigStorageErrorTitle,
        StartupFailure.ConfigKeyUnavailable => ConfigKeyUnavailableTitle,
        StartupFailure.ConfigKeyStoreMissing => ConfigKeyStoreMissingTitle,
        StartupFailure.ConfigMissing => ConfigMissingTitle,
        StartupFailure.ExceptionsImportUnreadable => ExceptionsImportUnreadableTitle,
        _ => StorageUnavailableTitle,
    };

    /// <summary>The stop's body. Only the failures that stop the app have one.</summary>
    public static string StopText(StartupFailure failure) => failure switch
    {
        StartupFailure.StorageUnavailable => StorageUnavailable,
        StartupFailure.DatabaseNewer => DatabaseNewer,
        StartupFailure.ConfigNewer => ConfigNewer,
        StartupFailure.ConfigUnreadable => ConfigUnreadable,
        StartupFailure.ConfigKeyMismatch => ConfigKeyMismatch,
        StartupFailure.ConfigLocked => ConfigLocked,
        StartupFailure.ConfigStorageError => ConfigStorageError,
        StartupFailure.ConfigKeyUnavailable => ConfigKeyUnavailable,
        StartupFailure.ConfigKeyStoreMissing => ConfigKeyStoreMissing,
        StartupFailure.ConfigMissing => ConfigMissing,
        StartupFailure.ExceptionsImportUnreadable => ExceptionsImportUnreadable,
        _ => StorageUnavailable,
    };

    // The stop's two ways on, each asked first
    public const string StartFreshTitle = "Start fresh?";
    public const string StartFreshButton = "Start fresh";
    public const string StartFreshBody = "The settings file is kept as " + AppPaths.UnreadableConfigFileName +
        " in the app data folder, in place of any older copy, but this version can't read it. A new file is made: the settings go back to their defaults, the exceptions must be entered again, and the AbuseIPDB download count starts again. The dataset, the AbuseIPDB list and your API keys are kept. This cannot be undone.";
    public const string StartFreshMissingBody = "If " + AppPaths.ConfigFileName +
        " is back in the app data folder, it is opened as it is. Otherwise a new settings file is made: the settings go back to their defaults, the exceptions must be entered again, and the AbuseIPDB download count starts again. The dataset, the AbuseIPDB list and your API keys are kept.";
    public const string StartWithoutOldExceptionsTitle = "Start without the old exceptions?";
    public const string StartWithoutOldExceptionsButton = "Start without exceptions";
    public const string StartWithoutOldExceptionsBody = "exceptions.json is kept as " + AppPaths.UnreadableExceptionsFileName +
        " in the app data folder, in place of any older copy. " + AppInfo.Name + " starts with no exceptions, so no network is cut out of the feeds until you enter them again in Settings. Your other settings are moved as usual.";
    public const string StartingFresh = "Starting fresh" + Ellipsis;
    public const string StartingWithoutOldExceptions = "Starting without the old exceptions" + Ellipsis;
    public const string StartFreshFailed = "The settings file couldn't be replaced. Please try again or contact the Infra Team.";
    public const string StartWithoutOldExceptionsFailed = "exceptions.json couldn't be moved. Please try again or contact the Infra Team.";

    // Results
    public const string SummaryInitial = "Search results will appear below here.";
    public const string Searching = "Searching" + Ellipsis;
    public const string ResultsInitial = "Results appear here after a search.";
    public const string ResultsNoDataset = "Download the dataset to start searching.";
    public const string ResultsInvalid = "Enter an ASN such as AS15169, or an IPv4 or IPv6 address.";
    public const string ResultsIPv6Disabled = "IPv6 is turned off in Settings.";
    public const string SearchFailed = "The search couldn't be completed. Please try again or contact the Infra Team.";
    public const string SearchCantStart = "The search couldn't be started now. Please try again.";
    public const string ExportTooltip = "Export every result to the export folder";
    public const string OpenFolder = "Open folder";
    public const string OpenFolderName = "Open export folder";
    public const string CopyTooltip = "Copy results";
    public const string CopyName = "Copy all results";
    public const string LargeRangeBadge = "Large range";
    public const string LargeRangeSpoken = ", large range";
    public const string ColumnNetwork = "Network";
    public const string ColumnCountry = "Country";
    public const string ColumnAs = "AS";
    public const string ColumnDomain = "Domain";
    public const string ExportIpNote = "Export is available for ASN searches.";
    public const string NothingExported = "Nothing was exported" + Dash + "every network is a large range that was left out";
    public const string ExportFolderUnavailable = "The export folder couldn't be used. Choose another folder in Settings.";
    public const string ExportWriteFailed = "The export couldn't be written. Please try again or contact the Infra Team.";

    /// <summary>
    /// An export whose write of one file failed, named first, then the files written before it and the old files left.
    /// Each ASN file is replaced by a move, so a failed file keeps its earlier copy whole, and the old files left can
    /// name it. A feed file is named with its folder, because the report and the combined feed share a file name. The
    /// report is always tried first, so the first file tried is the report and every later one a feed; the order, not a
    /// folder's name, tells them apart, so an export folder named like the feed folder changes nothing.
    /// </summary>
    public static string ExportFileFailed(string failedFile, IReadOnlyList<string> writtenBefore,
                                          IReadOnlyList<string>? oldFilesLeft, bool unsaved)
    {
        ArgumentNullException.ThrowIfNull(failedFile);
        ArgumentNullException.ThrowIfNull(writtenBefore);
        string report = writtenBefore.Count > 0 ? writtenBefore[0] : failedFile;
        return Name(failedFile) + " couldn't be written."
            + DoneBeforeFailure(writtenBefore, "written", Name)
            + (oldFilesLeft is { Count: > 0 }
                ? " The old " + NameList(oldFilesLeft.Select(Name))
                  + (oldFilesLeft.Count == 1 ? " was left as it was." : " were left as they were.")
                : "")
            + (unsaved && writtenBefore.Count > 0 ? UnsavedSettingsSentence : "") + TryAgain;

        // The folder is joined by the separator, as these texts name paths and build none
        string Name(string path) => string.Equals(path, report, StringComparison.Ordinal)
            ? Path.GetFileName(path)
            : ExportWriter.FortigateFolderName + Path.DirectorySeparatorChar + Path.GetFileName(path);
    }
    public const string CopyFailed = "The results couldn't be copied.";
    public const string FolderOpenFailed = "The folder couldn't be opened.";
    public const string WebPageOpenFailed = "The page couldn't be opened.";
    public const string OpenInIpinfo = "Open in ipinfo.io";
    public const string OpenInAbuseIpDb = "Open in AbuseIPDB";
    public const string ExceptionsUnreadable = "The exceptions list couldn't be read, so no feed was written. Open Settings to fix it.";

    // Check AbuseIPDB on IPInfo Lookup
    public const string CheckAbuse = "Check AbuseIPDB";
    public const string CheckingAbuse = "Checking AbuseIPDB" + Ellipsis;
    public const string AbuseNotSent = "Nothing was sent to AbuseIPDB" + Dash + "the address isn't public";
    public const string AbuseKeyCancelled = "Nothing was checked" + Dash + "AbuseIPDB needs an API key";
    public const string AbuseKeyReplaceCancelled = "Nothing was checked" + Dash + "AbuseIPDB didn't accept the saved key";
    public const string AbuseKeyNotAKey = "The AbuseIPDB key couldn't be saved" + Dash + "that isn't an API key";
    public const string AbuseKeySaveFailed = "The AbuseIPDB key couldn't be saved. Please try again or contact the Infra Team.";
    public const string AbuseKeyUnavailable = "The AbuseIPDB key couldn't be read. Allow access to it if asked, then try again.";
    public const string AbuseKeyUnreadable = "The AbuseIPDB key couldn't be read. Please try again or contact the Infra Team.";
    public const string AbuseKeyRejected = "AbuseIPDB didn't accept the key. Check it in your AbuseIPDB account, then try again.";
    public const string AbuseOffline = "AbuseIPDB couldn't be reached. Check the network connection, then try again.";
    public const string AbuseCheckFailed = "The AbuseIPDB check couldn't be completed. Please try again or contact the Infra Team.";
    public const string AbuseAnswerUnreadable = "AbuseIPDB's answer couldn't be read. Please try again or contact the Infra Team.";

    // Settings page: exceptions
    public const string ExceptionsTitle = "Exceptions";
    public const string ExceptionsMessage = "Networks listed here are cut out of every Fortigate feed before it's written, so no feed blocks them. The ASN report still lists every network.";
    public const string ExceptionNetworkName = "Network or address";
    public const string ExceptionNetworkPlaceholder = "Network or address";
    public const string ExceptionNoteName = "Note";
    public const string ExceptionNotePlaceholder = "Note (optional)";
    public const string AddException = "Add";
    public const string ExceptionsEmpty = "No exceptions yet.";
    public const string ExceptionInvalid = "Enter a network such as 203.0.113.0/24 or an address such as 203.0.113.7.";
    public const string ExceptionsUnreadableWarning = "The saved list couldn't be read. No feed is written until you save a new one.";
    public const string ExceptionsSaveFailed = "The exceptions couldn't be saved. Please try again or contact the Infra Team.";

    // API token dialog
    public const string TokenFirstTitle = "Enter API token";
    public const string TokenResetTitle = "Enter a new API token";
    public const string TokenBoxName = "API token";

    // The first-run body is the first sentence and the shared sentences; the reset body is the shared sentences alone
    private const string TokenFirstSentence = AppInfo.Name + " downloads the IPinfo Lite dataset with your IPinfo API token.";
    public const string TokenResetBody = "The token is in your IPinfo account under API token. It's saved on this computer for your account.";
    public const string TokenBody = TokenFirstSentence + " " + TokenResetBody;
    public const string ShowToken = "Show token";
    public const string SaveToken = "Save token";
    public const string TokenSaveFailed = "The API token couldn't be saved. Please try again or contact the Infra Team.";

    // AbuseIPDB key dialog: the first ask, and the ask after AbuseIPDB refused the saved key
    public const string AbuseKeyFirstTitle = "Enter AbuseIPDB key";
    public const string AbuseKeyReplaceTitle = "Enter a new AbuseIPDB key";
    public const string AbuseKeyBoxName = "AbuseIPDB key";
    private const string AbuseKeyWhere = "The key is in your AbuseIPDB account under API. It's saved on this computer for your account.";
    public const string AbuseKeyBody = "Check AbuseIPDB sends the searched address to AbuseIPDB with your API key. " + AbuseKeyWhere;
    public const string AbuseKeyReplaceBody = "AbuseIPDB didn't accept the saved key. " + AbuseKeyWhere;
    public const string ShowKey = "Show key";
    public const string SaveKey = "Save key";

    // Reset token confirmation
    public const string ResetTokenTitle = "Reset API token?";
    public const string ResetTokenBody = "The saved API token is removed from this computer and you're asked for a new one. The dataset is kept. This cannot be undone.";
    public const string ResetTokenButton = "Reset token";

    // Settings page: the heading, the sections and their buttons
    public const string SettingsCaption = "Export folder, feeds, API keys and the networks no feed blocks";
    public const string OptionsHeader = "Options";
    public const string OptionsCaption = "The export folder, IPv6, Fortigate feeds, large ranges and AbuseIPDB";
    public const string KeysHeader = "API keys";
    public const string KeysCaption = "Kept in this computer's key store for your account, never in the app's files";
    public const string SaveSettings = "Save settings";
    public const string SaveExceptions = "Save exceptions";
    public const string Revert = "Revert";
    public const string SetAbuseKey = "Set key";
    public const string RemoveAbuseKey = "Remove key";

    // Settings page: shown in place of its cards until start-up has read the saved settings and exceptions
    public const string SettingsNotStarted = "The settings are shown once " + AppInfo.Name + " has started.";

    // Settings page: the status and result lines
    public const string ChangesNotSaved = "Changes not saved";
    // On the Settings nav item while the page has unsaved edits, so it is seen from every page
    public const string SettingsNotSavedMark = "Not saved";
    public const string SettingsSaved = "Settings saved";
    // A save that moves the export folder: feeds go only to the new folder, so the earlier folder's copies are left as they were
    public const string SettingsSavedFolderChanged = SettingsSaved + Dash + "feeds already written to the earlier export folder stay there and aren't updated";
    public const string WholeNumberRequired = "Enter whole numbers only.";
    public const string FolderPickerFailed = "The folder picker couldn't be opened. Please try again or contact the Infra Team.";
    public const string TokenSavedStatus = "An IPinfo API token is saved on this computer";
    public const string NoTokenStatus = "No IPinfo API token is saved";
    public const string AbuseKeySavedStatus = "An AbuseIPDB key is saved on this computer";
    public const string NoAbuseKeyStatus = "No AbuseIPDB key is saved";
    public const string AbuseKeysUnavailable = "AbuseIPDB can't be used, because the app data folder couldn't be created";
    public const string KeysBusy = "Nothing was changed" + Dash + "wait until the current task ends";
    public const string TokenSaved = "API token saved";
    public const string TokenClearFailed = "The API token couldn't be removed. Please try again or contact the Infra Team.";
    public const string TokenRemovedNoNew = "API token removed" + Dash + "no new token was entered";
    public const string AbuseKeySaved = "AbuseIPDB key saved";
    public const string AbuseKeyRemoved = "AbuseIPDB key removed";
    public const string AbuseKeyRemoveFailed = "The AbuseIPDB key couldn't be removed. Please try again or contact the Infra Team.";

    // Settings page: the Remove key confirmation and the Set key prompt
    public const string AbuseKeyRemoveTitle = "Remove AbuseIPDB key?";
    public const string AbuseKeyRemoveButton = "Remove key";
    public const string AbuseKeyRemoveBody = "The saved AbuseIPDB key is removed from this computer. The AbuseIPDB list and its download count are kept, and a check asks for a key again. This cannot be undone.";
    public const string AbuseKeySetTitle = "Enter AbuseIPDB key";
    public const string AbuseKeySetBody = "The new key replaces any saved one. Nothing is sent to AbuseIPDB when you save it. " + AbuseKeyWhere;

    // Settings page: options
    public const string SettingsTitle = "Settings";
    public const string ExportFolderHeader = "Export folder";
    public const string ChooseFolder = "Choose folder";
    public const string UseDefault = "Use default";
    public const string ChooseFolderTitle = "Choose the export folder";
    public const string IPv6Header = "IPv6";
    public const string IPv6Enabled = "Include IPv6 networks";
    public const string IPv6Caption = "When off, IPv6 networks are left out of the dataset, searches and exports. Turning it back on needs a dataset update.";
    public const string FortigateHeader = "Fortigate threat feed";
    public const string FortigateEnabled = "Write a Fortigate threat feed with each export";
    public const string IncludeIPv4 = "Include IPv4 networks";
    public const string IncludeIPv6 = "Include IPv6 networks";
    public const string SplitByFamily = "Write IPv4 and IPv6 to separate files";
    public const string LargeRangesHeader = "Large ranges";
    public const string AskLargeRanges = "Ask before exporting large ranges";
    public const string IPv4PrefixLabel = "IPv4 \u2014 ask when the prefix is shorter than";
    public const string IPv6PrefixLabel = "IPv6 \u2014 ask when the prefix is shorter than";
    public const string AbuseIpDbHeader = "AbuseIPDB";
    public const string AbuseIpDbKeepDaysLabel = "Days of listings in the feed";
    public static string AbuseIpDbKeepDaysCaption =>
        "An address stays in the AbuseIPDB feed while it was listed within this many days of the last download. Each address is kept in the list for "
        + Count(AbuseIpDbLimits.MaxKeepDays, "day", "days") + " after it was last listed, so raising the number later brings back addresses it had left out.";
    public const string ChooseAddressFamily = "Choose at least one address family.";
    public const string EnterBothPrefixes = "Enter both prefix lengths.";
    public const string EnterKeepDays = "Enter the number of days to keep an address.";
    public const string SettingsSaveFailed = "The settings couldn't be saved. Please try again or contact the Infra Team.";

    // Close while the dataset is updating
    public const string CloseBusyTitle = "Cancel the update?";
    public const string CloseBusyBody = "The dataset is still updating. Closing " + AppInfo.Name + " cancels the update and then closes the application. The existing dataset is kept.";
    public const string CloseBusyButton = "Cancel and close";

    // Close while the Settings page has unsaved edits
    public const string CloseUnsavedTitle = "Discard the unsaved changes?";
    public const string CloseUnsavedBody = "The Settings page has changes that aren't saved. Closing " + AppInfo.Name + " discards them.";
    public const string CloseUnsavedButton = "Discard and close";

    // Close while the AbuseIPDB Blacklist page has a download, Clear list, Write feed or Empty feed with the service
    public const string CloseAbuseWorkTitle = "Stop the AbuseIPDB work?";
    public const string CloseAbuseDownloadBody = "AbuseIPDB Blacklist is still downloading. Closing " + AppInfo.Name + " stops a download AbuseIPDB hasn't answered yet, which can still count as one of today's downloads; an answer already received is added to the rolling list first. Then the application closes.";
    public const string CloseAbuseWorkBody = "AbuseIPDB Blacklist is still working on the list or the feed. Closing " + AppInfo.Name + " waits for it to finish and then closes the application.";
    public const string CloseAbuseWorkButton = "Stop and close";

    // Help dialog
    public const string HelpTokenHeading = "1. API token";
    public const string HelpTokenText = "Enter your IPinfo API token when asked. To change it, choose Reset token in Settings.";
    public const string HelpDatasetHeading = "2. Dataset";
    public const string HelpSearchHeading = "3. Search";
    public const string HelpExportHeading = "4. Export";
    public const string HelpExportText = "Export writes every network for the ASN to the export folder, with a Fortigate threat feed if that is turned on in Settings. Large ranges are left out unless you tick them. Google Cloud Lookup, Azure Lookup and AWS Lookup, in the list on the left, write a Fortigate feed of the Google Cloud ranges for one region, of the Azure ranges for one service tag, or of the AWS ranges for one service and region. Networks listed under Exceptions in Settings are cut out of every Fortigate feed.";

    public const string HelpAbuseHeading = "5. AbuseIPDB";

    // AbuseIPDB Blacklist page
    public const string AbuseIpDbTitle = "AbuseIPDB Blacklist";
    public const string AbuseIpDbCaption = "Download AbuseIPDB's blacklist and write a Fortigate threat feed from it";
    public const string DownloadBlacklist = "Download blacklist";
    public const string DownloadBlacklistTooltip = "Download AbuseIPDB's blacklist into the rolling list on this computer";
    public const string ClearList = "Clear list";
    public const string ClearListTooltip = "Remove every downloaded address from this computer";
    public const string EmptyFeed = "Empty feed";
    public const string EmptyFeedTooltip = "Rewrite the rolling feed files with no addresses. The AbuseIPDB list is kept.";
    public const string ColumnAddresses = "Addresses";
    public const string ColumnCountries = "Countries";
    public const string ColumnName = "Name";
    public const string AbuseNetworksHeader = "Offending networks";
    public const string AbuseGroupingBarName = "Network lookup progress";
    public const string ReadingAbuseList = "Reading the AbuseIPDB list" + Ellipsis;
    public const string DownloadingBlacklist = "Downloading the blacklist" + Ellipsis;
    public const string ClearingAbuseList = "Clearing the AbuseIPDB list" + Ellipsis;
    public const string WritingAbuseFeed = "Writing the feed" + Ellipsis;
    public const string EmptyingAbuseFeed = "Emptying the feed" + Ellipsis;
    public const string AbuseNetworksEmpty = "The networks of the listed addresses will appear here.";
    public const string AbuseNetworksNoAddresses = "No address within the keep days to group.";
    public const string AbuseNetworksNoAsn = "None of the addresses has an ASN in IPinfo.";

    // Clear list and Empty feed confirmations
    public const string ClearAbuseListTitle = "Clear the AbuseIPDB list?";
    public const string ClearAbuseListButton = "Clear list";
    public const string ClearAbuseListBody = "Every downloaded address is removed from this computer. The download count, the AbuseIPDB key and the feed files are kept; choose Empty feed to empty the files. This cannot be undone.";
    public const string EmptyAbuseFeedTitle = "Empty the AbuseIPDB feed?";
    public const string EmptyAbuseFeedButton = "Empty feed";
    public const string EmptyAbuseFeedBody = "Each AbuseIPDB feed file in the export folder's fortigate folder is rewritten with no addresses, so a FortiGate that reads it blocks none. The AbuseIPDB list is kept, and the next Write feed fills the files again.";

    // The download after the key prompt was cancelled or couldn't be shown
    public const string AbuseDownloadKeyCancelled = "Nothing was downloaded" + Dash + "AbuseIPDB needs an API key";
    public const string AbuseDownloadKeyReplaceCancelled = "Nothing was downloaded" + Dash + "AbuseIPDB didn't accept the saved key";

    public const string AbuseListCleared = "AbuseIPDB list cleared";
    public const string AbuseListClearFailed = "The AbuseIPDB list couldn't be cleared. Please try again or contact the Infra Team.";

    // The new addresses dialog: its Copy button, and the line under its list when the copy fails (Close is the shared one)
    public const string NewAddressesCopy = "Copy";
    public const string AbuseNewAddressesCopyFailed = "The addresses couldn't be copied.";

    // The New addresses button in the page's heading, the dialog's texts, and the lines when the press shows nothing
    public const string NewAddresses = "New addresses";
    public const string NewAddressesTooltip = "Show the addresses the last download added";
    public const string NewAddressesTitle = "New addresses";
    public const string NewAddressNoReportTime = "Unknown";
    public const string AbuseNewAddressesNotShown = "The new addresses couldn't be shown. Please try again or contact the Infra Team.";
    public const string AbuseNewAddressesBusy = "The new addresses weren't shown" + Dash + "press New addresses again when the current task ends";

    // Clear list and Empty feed when another task holds the work gate at the moment their question would show
    public const string AbuseClearListBusy = "The list wasn't cleared" + Dash + "press Clear list again when the current task ends";
    public const string AbuseEmptyFeedBusy = "The feed wasn't emptied" + Dash + "press Empty feed again when the current task ends";
    private const string AbuseNewAddressesNotKnown = "The new addresses aren't known for the last download.";

    // The rolling list's unusable states, shown on their own and before what a download, a write or a grouping did
    private const string AbuseListNewer = "The AbuseIPDB list was made by a newer version of " + AppInfo.Name + ", so it can't be used.";
    private const string AbuseListUnreadable = "The AbuseIPDB list couldn't be read. Clear the list to start again.";
    private const string AbuseListReadFailed = "The AbuseIPDB list couldn't be read now. Please try again or contact the Infra Team.";
    // Only for a download refused before anything was sent: a list that is newer, unreadable or can't be saved can
    // also come back after the request was sent and counted, so those lines don't say it
    private const string NothingSent = " Nothing was sent.";

    // Nav pane: the list of tools. Each item is labelled with its page title.
    public const string NavName = "Tools";

    // Tools: Google Cloud feed
    public const string GoogleCloudTitle = "Google Cloud Lookup";
    public const string GoogleCloudCaption = "Build a Fortigate threat feed from Google Cloud ranges";
    public const string AddGlobalRanges = "Add global ranges";
    public const string CheckRanges = "Check ranges";
    public const string WriteFeed = "Write feed";
    public const string WriteFeedTooltip = "Write the ticked ranges to a Fortigate threat feed";
    public const string CloudResultsHeader = "Ranges";
    public const string ColumnRegion = "Region";
    public const string ColumnStatus = "Status";
    public const string CloudSummaryInitial = "Checked ranges will appear below here.";
    public const string GoogleCloudEmptyInitial = "Choose a region, then choose Check ranges.";
    public const string GoogleCloudLoading = "Loading Google Cloud ranges" + Ellipsis;
    public const string GoogleCloudLoadOffline = "The Google Cloud ranges couldn't be loaded. Check the network connection, then try again.";
    public const string GoogleCloudLoadFailed = "The Google Cloud ranges couldn't be loaded. Please try again or contact the Infra Team.";
    public const string GoogleCloudLoadUnreadable = "The Google Cloud ranges couldn't be read.";
    public const string CloudNoDataset = "No dataset has been downloaded yet. Choose Update dataset before writing a feed.";
    public const string GoogleCloudNothingSelected = "No ranges match this region.";
    public const string CloudCheckFailed = "The ranges couldn't be checked. Please try again or contact the Infra Team.";
    public const string CloudSettingsChanged = "The feed couldn't be written. The settings changed after the ranges were checked. Choose Check ranges again.";
    public const string CloudWriteFailed = "The feed couldn't be written. Please try again or contact the Infra Team.";
    public const string CloudLargeRangesBusy = "The feed wasn't written" + Dash + "press Write feed again when the current task ends";

    /// <summary>
    /// A cloud feed write that failed: the failed file first when it is known, then the files written before it and the
    /// old files left. With none of them and nothing unsaved it is <see cref="CloudWriteFailed"/>.
    /// </summary>
    public static string CloudWriteFailedLine(string? failedFile, IReadOnlyList<string> writtenBefore,
                                              IReadOnlyList<string>? oldFilesLeft, bool unsaved)
    {
        ArgumentNullException.ThrowIfNull(writtenBefore);
        return (failedFile is null ? "The feed couldn't be written." : FailedFileLead(failedFile, "written"))
            + DoneBeforeFailure(writtenBefore, "written") + OldFilesLeftSentence(oldFilesLeft)
            + (unsaved && writtenBefore.Count > 0 ? UnsavedSettingsSentence : "") + TryAgain;
    }
    public const string CloudLargeRangesTitle = "Write the feed?";
    public const string AzureTitle = "Azure Lookup";
    public const string AzureCaption = "Build a Fortigate threat feed from Azure service tags";
    public const string CheckForUpdates = "Check for updates";
    public const string CheckForUpdatesTooltip = "Look for a newer Azure service tags file";
    public const string ColumnService = "Service";
    public const string AzureAllRegions = "All regions";
    public const string AzureEmptyInitial = "Choose a service and a region, then choose Check ranges.";
    public const string AzureLoading = "Loading Azure service tags" + Ellipsis;
    public const string AzureLoadOffline = "The Azure service tags couldn't be loaded. Check the network connection, then try again.";
    public const string AzureLoadFailed = "The Azure service tags couldn't be loaded. Please try again or contact the Infra Team.";
    public const string AzureLoadUnreadable = "The Azure service tags couldn't be read.";
    public const string AzureNothingSelected = "No ranges are listed for this service tag.";
    public const string AzureStatusUnavailable = "No Azure service tags are available.";
    public const string AwsTitle = "AWS Lookup";
    public const string AwsCaption = "Build a Fortigate threat feed from AWS IP ranges";
    public const string AwsCheckForUpdatesTooltip = "Look for a newer AWS IP ranges file";
    public const string AwsAllRegions = "All regions";
    public const string AwsGlobalRegion = "Global";
    public const string AwsEmptyInitial = "Choose a service and a region, then choose Check ranges.";
    public const string AwsLoading = "Loading AWS IP ranges" + Ellipsis;
    public const string AwsLoadOffline = "The AWS IP ranges couldn't be loaded. Check the network connection, then try again.";
    public const string AwsLoadFailed = "The AWS IP ranges couldn't be loaded. Please try again or contact the Infra Team.";
    public const string AwsLoadUnreadable = "The AWS IP ranges couldn't be read.";
    public const string AwsNothingSelected = "No ranges are listed for this service and region.";
    public const string AwsStatusUnavailable = "No AWS IP ranges are available.";
    public const string StatusConfirmed = "Confirmed";
    public const string StatusMixed = "Mixed";
    public const string StatusNotInDatabase = "Not in IPinfo";
    public const string CloudNothingTicked = "Nothing was written" + Dash + "no ranges were ticked";
    public const string CloudAllLeftOut = "Nothing was written" + Dash + "every ticked range is a large range that was left out";
    public const string CloudAllExcepted = "Nothing was written" + Dash + "exceptions cover every ticked range";
    public const string CloudAllLeftOutOrExcepted = "Nothing was written" + Dash + "every ticked range was left out as a large range or removed by exceptions";

    // Menus
    public const string MenuSettings = "Settings" + Ellipsis;
    public const string MenuResetToken = "Reset API token" + Ellipsis;

    /// <summary>The dataset facts line: the download date, the range count and, for an IPv4-only dataset, a note.</summary>
    public static string DatasetFacts(DateOnly downloadedOn, long rangeCount, bool includesIPv6) =>
        "Downloaded " + downloadedOn.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) + Joiner
        + Count(rangeCount, "range", "ranges") + (includesIPv6 ? "" : Joiner + IPv4Only);

    public static string ShowingCapped(int shown, int total, string asn, string asName) =>
        $"Showing {Number(shown)} of {Number(total)} networks for {asn}" + AsnName(asName);

    public static string AllShown(int total, string asn, string asName) =>
        Count(total, "network", "networks") + " for " + asn + AsnName(asName);

    public static string IpContained(int count, string ip) =>
        Count(count, "network", "networks") + (count == 1 ? " contains " : " contain ") + ip;

    public static string NoAsnResults(string asn) => $"No networks found for {asn}.";

    public static string NoIpResult(string ip) => $"No network found for {ip}.";

    public static string Exported(int networks, int files) =>
        "Exported " + Count(networks, "network", "networks") + " to " + Count(files, "file", "files");

    public static string LargeRangesLeftOut(int count) =>
        Dash + Count(count, "large range", "large ranges") + " left out";

    public static string Copied(int count) => "Copied " + Count(count, "result", "results");

    // After a feed's result line while the Settings page holds edits: the write used what was saved
    public const string UnsavedSettingsNote = Dash + "unsaved changes on the Settings page weren't used";

    // The same as a sentence, on a failure line after the files done before the failure
    private const string UnsavedSettingsSentence = " Unsaved changes on the Settings page weren't used.";

    /// <summary>What exceptions did to a feed, after a dash: the ranges they cut smaller and the ranges they removed.</summary>
    public static string ExceptionsApplied(int cut, int removed, string singular, string plural)
    {
        if (cut > 0 && removed > 0)
            return Dash + Count(cut, singular, plural) + " cut and " + Number(removed) + " removed by exceptions";
        if (cut > 0)
            return Dash + Count(cut, singular, plural) + " cut by exceptions";
        return removed > 0 ? Dash + Count(removed, singular, plural) + " removed by exceptions" : "";
    }

    /// <summary>The feed files from an earlier write that were left in place, by file name, after a dash.</summary>
    public static string OldFeedFilesLeft(IReadOnlyList<string>? paths)
    {
        if (paths is not { Count: > 0 })
            return "";

        string names = NameList(paths.Select(Path.GetFileName));
        return Dash + "the old " + names + (paths.Count == 1 ? " was left as it was" : " were left as they were");
    }

    // The same as a sentence, on a failure line
    private static string OldFilesLeftSentence(IReadOnlyList<string>? paths) =>
        paths is not { Count: > 0 }
            ? ""
            : " The old " + FileNames(paths) + (paths.Count == 1 ? " was left as it was." : " were left as they were.");

    public static string ExceptionAdded(string network) => network + " was added.";

    public static string ExceptionAddedMasked(string network) => network + " was added, host bits cleared.";

    public static string ExceptionRemoved(string network) => network + " was removed.";

    public static string ExceptionDuplicate(string network) => network + " is already in the list.";

    public static string ExceptionsFull(int max) => "The list can hold at most " + Count(max, "exception", "exceptions") + ".";

    public static string ExceptionRemoveName(string network) => "Remove " + network;

    public static string ExceptionsCount(int count) => Count(count, "exception", "exceptions");

    public static string ExceptionsSaved(int count) => "Saved " + Count(count, "exception", "exceptions");

    public static string CheckAbuseTooltip =>
        "Send the address to AbuseIPDB for its score over the last " + Count(AbuseIpDbLimits.CheckMaxAgeInDays, "day", "days");

    /// <summary>The results line after a check: the score, and the reports behind it or that there were none.</summary>
    public static string AbuseScore(AbuseCheck check)
    {
        string head = "AbuseIPDB score " + Number(check.Score) + " of " + Number(AbuseIpDbLimits.MaxScore) + ": ";
        if (check.TotalReports == 0)
        {
            return head + "no reports in the last " + Count(AbuseIpDbLimits.CheckMaxAgeInDays, "day", "days") + ".";
        }

        // The date is the UTC date, as the feed headers are, so it reads the same on every machine
        string last = check.LastReportedAt is { } reported
            ? ", last on " + reported.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "";
        return head + Count(check.TotalReports, "report", "reports") + " from "
            + Count(check.DistinctUsers, "user", "users") + last + ".";
    }

    /// <summary>
    /// AbuseIPDB's daily limit, with the UTC time it lifts when AbuseIPDB said, rounded up to the next whole minute so
    /// the time named is never before the limit lifts.
    /// </summary>
    public static string AbuseRateLimited(DateTimeOffset? retryAt) =>
        "AbuseIPDB's daily limit has been reached. Please try again "
        + (retryAt is { } at ? "after " + UpToMinute(at.UtcDateTime).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC." : "later.");

    // An exact minute stays; a time within a minute of DateTime.MaxValue stays too, because the next minute doesn't exist
    private static DateTime UpToMinute(DateTime time)
    {
        long past = time.Ticks % TimeSpan.TicksPerMinute;
        return past == 0 || time.Ticks > DateTime.MaxValue.Ticks - TimeSpan.TicksPerMinute
            ? time
            : time.AddTicks(TimeSpan.TicksPerMinute - past);
    }

    public static string RateLimited(int downloadsPerDay, TimeSpan? retryAfter)
    {
        string head = $"IPinfo allows {Number(downloadsPerDay)} downloads a day and the limit has been reached.";
        if (retryAfter is null)
        {
            return head + " Please try again later.";
        }

        int minutes = Math.Max(1, (int)Math.Ceiling(retryAfter.Value.TotalMinutes));
        return head + " Please try again in about " + Count(minutes, "minute", "minutes") + ".";
    }

    public static string DownloadProgress(double doneMb, double totalMb) =>
        $"{doneMb.ToString("0.0", CultureInfo.InvariantCulture)} MB of {totalMb.ToString("0.0", CultureInfo.InvariantCulture)} MB";

    public static string DownloadedSoFar(double doneMb) =>
        $"{doneMb.ToString("0.0", CultureInfo.InvariantCulture)} MB downloaded";

    public static string ImportPercentRead(int percent) => $"{percent}% read";

    public static string RegionChoice(string scope, int count) =>
        scope + Joiner + Count(count, "range", "ranges");

    public static string AzureStatusUpToDate(DateOnly fileDate, long changeNumber, TimeOnly checkedAt) =>
        AzureFile(fileDate, changeNumber) + Joiner + "up to date, checked " + checkedAt.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string AzureStatusUpdated(DateOnly fileDate, long changeNumber, TimeOnly checkedAt) =>
        AzureFile(fileDate, changeNumber) + Joiner + "updated, checked " + checkedAt.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string AzureStatusCheckFailed(DateOnly fileDate, long changeNumber) =>
        AzureFile(fileDate, changeNumber) + Joiner + "couldn't check for a newer file";

    private static string AzureFile(DateOnly fileDate, long changeNumber) =>
        "Service tags of " + fileDate.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
        + Joiner + "change " + changeNumber.ToString(CultureInfo.InvariantCulture);

    public static string AwsStatusUpToDate(DateTimeOffset created, TimeOnly checkedAt) =>
        AwsFile(created) + Joiner + "up to date, checked " + checkedAt.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string AwsStatusUpdated(DateTimeOffset created, TimeOnly checkedAt) =>
        AwsFile(created) + Joiner + "updated, checked " + checkedAt.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string AwsStatusCheckFailed(DateTimeOffset created) =>
        AwsFile(created) + Joiner + "couldn't check for a newer file";

    // The file's own creation time is UTC, so it is shown as UTC and not converted
    private static string AwsFile(DateTimeOffset created) =>
        "AWS ranges of " + created.UtcDateTime.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC";

    public static string CloudSummary(int total, int confirmed, int mixed, int notInDatabase) =>
        Count(total, "range", "ranges") + ": "
        + Number(confirmed) + " confirmed, "
        + Number(mixed) + " mixed, "
        + Number(notInDatabase) + " not in IPinfo";

    public static string CloudFeedWritten(int ranges, int files) =>
        "Wrote " + Count(ranges, "range", "ranges") + " to " + Count(files, "file", "files");

    public static string CloudUnconfirmed(int count) =>
        Dash + Count(count, "range", "ranges") + " added without confirmation";

    public static string CloudLargeRangesMessage(int count, int ipv4MinPrefix, int ipv6MinPrefix)
    {
        string limits = $"(IPv4 /{ipv4MinPrefix}, IPv6 /{ipv6MinPrefix})";
        return count == 1
            ? $"1 network is larger than the limits in Settings {limits}. It's left out of the feed unless you tick it."
            : $"{Number(count)} networks are larger than the limits in Settings {limits}. They're left out of the feed unless you tick them.";
    }

    public static string LargeRangesTitle(string asn) => "Export " + asn + "?";

    public static string LargeRangesMessage(int count, int ipv4MinPrefix, int ipv6MinPrefix)
    {
        string limits = $"(IPv4 /{ipv4MinPrefix}, IPv6 /{ipv6MinPrefix})";
        return count == 1
            ? $"1 network is larger than the limits in Settings {limits}. It's left out of the export unless you tick it."
            : $"{Number(count)} networks are larger than the limits in Settings {limits}. They're left out of the export unless you tick them.";
    }

    public static string LargeRangesTicked(int ticked, int total) => $"{Number(ticked)} of {Number(total)} ticked";

    public static string Addresses(BigInteger count) =>
        count.IsOne ? "1 address" : count.ToString("N0", CultureInfo.InvariantCulture) + " addresses";

    public static string Subnets64(BigInteger count) =>
        count.IsOne ? "1 /64 subnet" : count.ToString("N0", CultureInfo.InvariantCulture) + " /64 subnets";

    public static string HelpDatasetText() =>
        $"{AppInfo.Name} checks for a newer dataset each time it opens. Choose Update dataset to download it. IPinfo allows {IpInfoLimits.DownloadsPerDay} downloads a day.";

    public static string HelpSearchText() =>
        $"Enter an ASN such as AS15169 or an IP address, then choose Search. The list shows up to {MainViewModel.ResultLimit} networks. "
        + "Right-click a result, or click it with two fingers on a trackpad, to open the searched address or ASN on "
        + "ipinfo.io, or the address on AbuseIPDB's website.";

    public static string HelpAbuseText() =>
        "Check AbuseIPDB, on " + Title + ", sends a searched address to AbuseIPDB for its score. On " + AbuseIpDbTitle
        + ", Download blacklist adds AbuseIPDB's blacklist to a rolling list on this computer; the free plan allows "
        + $"{AbuseIpDbLimits.FreeBlacklistDownloadsPerDay} downloads a day. "
        + "New addresses lists the addresses the last download added. Right-click an offending network to open its ASN "
        + "on ipinfo.io, or a new address to open it on ipinfo.io or AbuseIPDB's website. Write feed writes a Fortigate threat feed of the "
        + "addresses listed in the days before the last download, as many days as Settings sets, leaving out the networks "
        + "under Exceptions. "
        + "With IPv6 off, the download asks for IPv4 addresses only, and an existing IPv6 feed file is emptied. Empty feed rewrites the feed files with no addresses, so a "
        + "FortiGate that reads them blocks none. " + FortiOsPool;

    // From FortiOS 7.4.4 the threat feed entries are counted per device, across every IP address feed
    private static string FortiOsPool =>
        "From FortiOS 7.4.4 every IP address feed on a FortiGate shares one entry pool (300,000 on entry-level models), "
        + "and two full rolling files hold " + Number(2L * AbuseIpDbLimits.MaxFeedEntries) + " addresses.";

    public static string WriteAbuseFeedTooltip(bool ipv6Enabled) =>
        "Write a Fortigate threat feed of the addresses listed in the days before the last download, as many days as "
        + "Settings sets."
        + (ipv6Enabled ? "" : " With IPv6 off, an existing IPv6 feed file is emptied.") + " " + FortiOsPool;

    /// <summary>The offending networks row button's tooltip and automation name.</summary>
    public static string LookUpNetworkName(uint asn) =>
        "Look up AS" + asn.ToString(CultureInfo.InvariantCulture) + " on " + Title;

    /// <summary>The offending networks while they are grouped, with how far the lookup has got once it is known.</summary>
    public static string GroupingNetworks(GroupingProgress? progress) =>
        progress is { Total: > 0 } p
            ? "Grouping " + Number(p.Done) + " of " + Count(p.Total, "address", "addresses") + " by network" + Ellipsis
            : "Grouping the addresses by network" + Ellipsis;

    /// <summary>
    /// The page's facts row as one line, the whole text its tooltip and screen readers get: the head, then the detail
    /// when there is one (<see cref="AbuseFactsHead"/>, <see cref="AbuseFactsDetail"/>).
    /// </summary>
    public static string AbuseFacts(AbuseListStatus status, int keepDays, DateTimeOffset now)
    {
        string detail = AbuseFactsDetail(status, keepDays, now);
        return detail.Length == 0 ? AbuseFactsHead(status, now) : AbuseFactsHead(status, now) + Joiner + detail;
    }

    /// <summary>
    /// The facts row's first line: the last download, or "Not downloaded yet", which follows the last download, not the
    /// file, so a cleared list says it too. A list that is newer, unreadable or can't be read now gives only the
    /// downloads left, because the status line names its state. <paramref name="now"/> tells a stored limit whose reset
    /// has passed from one still in force.
    /// </summary>
    public static string AbuseFactsHead(AbuseListStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.State is AbuseListState.Newer or AbuseListState.Unreadable or AbuseListState.StorageError)
            return DownloadsLeft(status, now);
        if (status.LastDownloadAt is not { } last)
            return "Not downloaded yet";

        return "Downloaded " + UtcMinuteDown(last);
    }

    /// <summary>
    /// The facts row's second line: the downloads left first, so they show in a narrow window, then the addresses within
    /// the keep days once there was a download. Empty while this computer's clock is behind the last download, because
    /// the counts are withheld then and the downloads left are judged by that clock; the status line says why. Empty for
    /// a list that is newer, unreadable or can't be read now, whose first line already gives the downloads left.
    /// </summary>
    public static string AbuseFactsDetail(AbuseListStatus status, int keepDays, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.State is AbuseListState.Newer or AbuseListState.Unreadable or AbuseListState.StorageError
            || status.ClockBehind)
        {
            return "";
        }
        if (status.LastDownloadAt is null)
            return DownloadsLeft(status, now);

        return DownloadsLeft(status, now) + Joiner
            + Number(status.IPv4Count) + " IPv4 and " + Count(status.IPv6Count, "IPv6 address", "IPv6 addresses")
            + " in the last " + Count(keepDays, "day", "days");
    }

    // AbuseIPDB's own remaining count while it is current: no reset, or a reset still ahead and no further than the
    // service believes. A stored limit whose reset has passed (or is too far ahead to trust) is a new day's allowance
    // when its total is known; otherwise, as with no remaining count, the 2xx answers this UTC day of the free limit.
    private static string DownloadsLeft(AbuseListStatus status, DateTimeOffset now)
    {
        if (status.RateLimit is { Remaining: { } remaining } limit)
        {
            bool current = limit.ResetAt is not { } reset
                || (reset > now && reset - now <= TimeSpan.FromHours(AbuseIpDbLimits.MaxResetAheadHours));
            if (!current)
            {
                if (limit.Limit is { } allowance)
                    return Number(allowance) + " of " + Count(allowance, "download", "downloads") + " left today";
                return DownloadsUsed(status);
            }

            int left = Math.Max(0, remaining);
            return limit.Limit is { } total
                ? Number(left) + " of " + Count(total, "download", "downloads") + " left today"
                : Count(left, "download", "downloads") + " left today";
        }

        return DownloadsUsed(status);
    }

    private static string DownloadsUsed(AbuseListStatus status) =>
        Number(status.DownloadsToday) + " of "
        + Count(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay, "download", "downloads") + " used today";

    public static string AbuseNextDownloadAfter(DateTimeOffset at) => "The next download can be sent after " + UtcMinute(at) + ".";

    /// <summary>The status line when the last download is older than the keep days: the feed still holds that download's addresses.</summary>
    public static string AbuseListStale(int keepDays) =>
        "The last download is more than " + Count(keepDays, "day", "days") + " old, so the feed holds what was listed in the "
        + Count(keepDays, "day", "days") + " before it. Download the blacklist to bring it up to date.";

    /// <summary>
    /// The status line when this computer's clock is more than the clock guard's bound behind the last download: the
    /// addresses aren't counted until the clock is put right.
    /// </summary>
    public static string AbuseClockBehind =>
        "This computer's clock is more than " + ClockDifference + " behind the last download, so the addresses aren't counted. "
        + "Correct the clock.";

    /// <summary>The line for a list that can't be used; a missing or usable list gives the empty string.</summary>
    public static string AbuseListStateLine(AbuseListState state) => state switch
    {
        AbuseListState.Newer => AbuseListNewer,
        AbuseListState.Unreadable => AbuseListUnreadable,
        AbuseListState.StorageError => AbuseListReadFailed,
        _ => "",
    };

    /// <summary>What a download added, with the expired addresses and the skipped entries only when there are any.</summary>
    public static string AbuseDownloaded(AbuseDownloadResult result, int keepDays)
    {
        ArgumentNullException.ThrowIfNull(result);
        string line = "Blacklist downloaded: " + Count(result.Listed, "address", "addresses") + ", "
            + Number(result.Added) + " new, " + Number(result.Lapsed) + " dropped out of the last " + Count(keepDays, "day", "days");
        if (result.Expired > 0)
            line += ", " + Number(result.Expired) + " removed after " + Count(AbuseIpDbLimits.MaxKeepDays, "day", "days") + " unlisted";
        if (result.Skipped > 0)
            line += Dash + (result.Skipped == 1 ? "1 entry was left out" : Number(result.Skipped) + " entries were left out");
        return line;
    }

    /// <summary>
    /// A download that failed, or the one download sent again after a key was saved; a prompt that was cancelled or
    /// couldn't open uses <see cref="AbuseDownloadKeyCancelled"/> or <see cref="AbuseDownloadKeyReplaceCancelled"/>
    /// instead. <paramref name="at"/> is the refusal or retry time the view model chose. <paramref name="counted"/> says
    /// AbuseIPDB answered with a 2xx, so a list that went wrong after it still names the download as one of today's.
    /// </summary>
    public static string AbuseDownloadFailure(AbuseIpDbFailure failure, DateTimeOffset? at, bool counted = false)
    {
        string line = DownloadFailureLine(failure, at);

        // The unreadable answer and the clock already say the download counts
        return counted && failure is not (AbuseIpDbFailure.BadResponse or AbuseIpDbFailure.ClockWrong)
            ? line + DownloadStillCounts
            : line;
    }

    private const string DownloadStillCounts = " The blacklist was downloaded, so it still counts as one of today's downloads.";

    private static string DownloadFailureLine(AbuseIpDbFailure failure, DateTimeOffset? at) => failure switch
    {
        AbuseIpDbFailure.NoKey => AbuseKeyUnreadable,
        AbuseIpDbFailure.KeyRejected => AbuseKeyRejected,
        AbuseIpDbFailure.KeyUnavailable => AbuseKeyUnavailable,
        AbuseIpDbFailure.RateLimited => AbuseRateLimited(at),
        AbuseIpDbFailure.LimitReached => "No downloads are left today." + NothingSent + " Please try again "
            + (at is { } time ? "after " + UtcMinute(time) + "." : "later."),
        AbuseIpDbFailure.Offline => AbuseOffline,
        AbuseIpDbFailure.ServiceError => "The blacklist couldn't be downloaded. Please try again or contact the Infra Team.",
        AbuseIpDbFailure.BadResponse => "AbuseIPDB's answer couldn't be read, so nothing was added. It still counts as one of today's downloads.",
        AbuseIpDbFailure.ListNewer => AbuseListNewer,
        AbuseIpDbFailure.ListUnreadable => AbuseListUnreadable,
        AbuseIpDbFailure.StorageError => "The AbuseIPDB list couldn't be read or saved. Please try again or contact the Infra Team.",
        AbuseIpDbFailure.ClockWrong => "The blacklist wasn't added, because this computer's clock is more than " + ClockDifference
            + " from AbuseIPDB's. Correct the clock, then try again. The download still counts.",
        AbuseIpDbFailure.DiskNearlyFull => "There isn't enough free space for the blacklist. Free some space, then try again." + NothingSent,
        AbuseIpDbFailure.ClockBehind => "Nothing was sent, because this computer's clock is more than " + ClockDifference
            + " behind the last download. Correct the clock, then try again.",
        _ => Generic,
    };

    // The clock guard's bound, in days when it is whole days
    private static string ClockDifference
    {
        get
        {
            int hours = AbuseIpDbLimits.MaxClockDifferenceHours;
            if (hours % 24 != 0)
                return Count(hours, "hour", "hours");
            return hours == 24 ? "a day" : Count(hours / 24, "day", "days");
        }
    }

    /// <summary>A written feed: the counts and the export folder, then the exceptions, the cap and each emptied file when there are any.</summary>
    public static string AbuseFeedWritten(AbuseFeedWriteResult result, string folder)
    {
        ArgumentNullException.ThrowIfNull(result);
        string line = "Feed written: " + Number(result.IPv4Written) + " IPv4 and "
            + Count(result.IPv6Written, "IPv6 address", "IPv6 addresses") + " to " + folder;
        if (result.ExceptionRemovals > 0)
            line += Dash + Count(result.ExceptionRemovals, "address", "addresses") + " removed by exceptions";
        if (result.LeftOut > 0)
        {
            line += Dash + Count(result.LeftOut, "older address", "older addresses") + " left out over the limit of "
                + Number(AbuseIpDbLimits.MaxFeedEntries) + " a file";
        }

        if (result.EmptiedFiles is { Count: > 0 } emptied)
            line += Dash + FileNames(emptied) + (emptied.Count == 1 ? " was emptied" : " were emptied");
        return line;
    }

    /// <summary>
    /// A feed write that failed. With IPv6 off only IPv4 addresses are counted, so nothing to write names the family;
    /// a failed write names the failed file first, when it is known, then the files written and emptied before it, the
    /// old files it left, and the unsaved Settings edits when it had done any.
    /// </summary>
    public static string AbuseFeedWriteFailureLine(AbuseFeedWriteResult result, int keepDays, bool ipv6Enabled,
                                                   bool unsaved = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        string family = ipv6Enabled ? "address" : "IPv4 address";
        string days = Count(keepDays, "day", "days");
        return result.Failure switch
        {
            AbuseFeedWriteFailure.ExceptionsUnreadable => ExceptionsUnreadable,
            AbuseFeedWriteFailure.ListNewer => AbuseListNewer,
            AbuseFeedWriteFailure.ListUnreadable => AbuseListUnreadable,
            AbuseFeedWriteFailure.StorageError => AbuseListReadFailed,
            AbuseFeedWriteFailure.NoDownload => "Nothing was written" + Dash + "download the blacklist first",
            AbuseFeedWriteFailure.ClockWrong => "The feed wasn't written, because this computer's clock is more than " + ClockDifference
                + " behind the last download. Correct the clock, then try again.",
            AbuseFeedWriteFailure.NothingToWrite => "Nothing was written" + Dash + (result.ExceptionRemovals > 0
                ? "exceptions cover every " + family + " listed in the last " + days
                : "no " + family + " was listed in the last " + days),
            AbuseFeedWriteFailure.FolderUnavailable => ExportFolderUnavailable,
            AbuseFeedWriteFailure.WriteFailed =>
                (result.FailedFile is { } failed ? FailedFileLead(failed, "written") : "The feed couldn't be written.")
                + DoneBeforeFailure(result.Files.Where(f => !IsEmptied(f)).ToList(), "written")
                + DoneBeforeFailure(result.Files.Where(IsEmptied).ToList(), "emptied")
                + OldFilesLeftSentence(result.OldFilesLeft)
                + (unsaved && result.Files.Count > 0 ? UnsavedSettingsSentence : "") + TryAgain,
            _ => Generic,
        };

        // A family with no address whose file existed was rewritten header-only, so it was emptied, not written
        bool IsEmptied(string file) => result.EmptiedFiles.Contains(file, StringComparer.Ordinal);
    }

    public static string AbuseFeedEmptied(AbuseFeedWriteResult result, string folder)
    {
        ArgumentNullException.ThrowIfNull(result);
        return "Feed emptied: " + FileNames(result.Files) + " in " + folder;
    }

    /// <summary>
    /// An Empty feed that failed; a failed write names the failed file first, when it is known, then the files emptied
    /// before it and the old files it left. Like the Empty feed line it has no unsaved note: Empty feed writes no address.
    /// </summary>
    public static string AbuseFeedEmptyFailureLine(AbuseFeedWriteResult result, string folder)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Failure switch
        {
            AbuseFeedWriteFailure.NothingToWrite => "Nothing was emptied" + Dash + "there's no AbuseIPDB feed file in " + folder,
            AbuseFeedWriteFailure.WriteFailed =>
                (result.FailedFile is { } failed ? FailedFileLead(failed, "emptied") : "The feed couldn't be emptied.")
                + DoneBeforeFailure(result.Files, "emptied") + OldFilesLeftSentence(result.OldFilesLeft) + TryAgain,
            _ => Generic,
        };
    }

    private const string TryAgain = " Please try again or contact the Infra Team.";

    // The files a failed write or empty had already replaced, or nothing when it had replaced none; name gives each
    // file's name in the line, its file name unless the caller says otherwise
    private static string DoneBeforeFailure(IReadOnlyList<string> files, string verb, Func<string, string>? name = null) =>
        files.Count == 0
            ? ""
            : " " + NameList(files.Select(name ?? (f => Path.GetFileName(f))))
                + (files.Count == 1 ? " was " : " were ") + verb + " before the failure.";

    // A failure line's opening when the failed file is known: its name, then what couldn't be done to it
    private static string FailedFileLead(string failedFile, string verb) =>
        Path.GetFileName(failedFile) + " couldn't be " + verb + ".";

    /// <summary>
    /// The offending networks' summary: first that it is a report, which the exceptions don't cut, so the note shows
    /// whenever the line trims; then the networks (and the rows shown when capped) for the addresses grouped, the two
    /// buckets and the addresses the cap left out.
    /// </summary>
    public static string AbuseNetworksSummary(AbuseNetworksResult result, int shown, bool ipv6Enabled)
    {
        ArgumentNullException.ThrowIfNull(result);
        int total = result.Networks.Count;
        string line = "A report, so exceptions aren't applied: "
            + (shown < total
                ? "showing " + Number(shown) + " of " + Number(total) + " networks"
                : Count(total, "network", "networks"))
            + " for " + Count((long)result.IPv4Addresses + result.IPv6Addresses, "address", "addresses");

        // On an IPv4-only dataset every IPv6 address looked up lands in the not-in-IPinfo bucket
        bool noIPv6Networks = ipv6Enabled && result.IncludesIPv6 && result.Dataset is { IncludesIPv6: false };
        line += Joiner + Count(result.NoAsn, "address", "addresses") + " with no ASN in IPinfo"
            + Joiner + Count(result.NotInIpinfo, "address", "addresses") + " not in IPinfo"
            + (noIPv6Networks ? " (the dataset holds no IPv6 networks)" : "");
        if (result.LeftOut > 0)
            line += Joiner + Count(result.LeftOut, "older address", "older addresses") + " not looked up";
        return line;
    }

    public static string AbuseNetworksFailureLine(AbuseNetworksFailure failure) => failure switch
    {
        AbuseNetworksFailure.ListNewer => AbuseListNewer,
        AbuseNetworksFailure.ListUnreadable => AbuseListUnreadable,
        AbuseNetworksFailure.StorageError => AbuseListReadFailed,
        AbuseNetworksFailure.NoDownload => "Nothing was grouped" + Dash + "download the blacklist first",
        AbuseNetworksFailure.NoDataset => "Update the IPinfo dataset to group the addresses by network.",
        AbuseNetworksFailure.LookupFailed => "The addresses couldn't be grouped. Please try again or contact the Infra Team.",
        AbuseNetworksFailure.DatasetChanged => "The dataset changed while the addresses were grouped. Please try again.",
        AbuseNetworksFailure.ClockWrong => "The networks weren't grouped, because this computer's clock is more than "
            + ClockDifference + " behind the last download. Correct the clock.",
        _ => Generic,
    };

    /// <summary>The new addresses dialog's message: a report, the download that added them (UTC, cut to the minute) and
    /// what each line gives.</summary>
    public static string NewAddressesMessage(DateTimeOffset asOf) =>
        "A report, so exceptions aren't applied. Added to the list by the download of " + UtcMinuteDown(asOf) + "."
        + " Each line gives the address, its AbuseIPDB score out of 100 and the day of its last report.";

    /// <summary>"N new addresses", or "Showing 300 of N new addresses" when the dialog lists fewer than there are.</summary>
    public static string NewAddressesCount(int shown, int total)
    {
        string all = Count(total, "new address", "new addresses");
        return shown < total ? "Showing " + Number(shown) + " of " + all : all;
    }

    /// <summary>The score alone; the message says it is out of 100.</summary>
    public static string NewAddressScore(int score) => Number(score);

    /// <summary>The last report's UTC date, or that there is none.</summary>
    public static string NewAddressLastReported(DateTimeOffset? at) =>
        at is { } time ? time.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : NewAddressNoReportTime;

    public static string NewAddressesCopied(int count) => "Copied " + Count(count, "address", "addresses");

    /// <summary>Why the press showed no dialog: the list's own state line, or that the set isn't known.</summary>
    public static string AbuseNewAddressesFailureLine(AbuseNewAddressesFailure failure) => failure switch
    {
        AbuseNewAddressesFailure.ListNewer => AbuseListNewer,
        AbuseNewAddressesFailure.ListUnreadable => AbuseListUnreadable,
        AbuseNewAddressesFailure.StorageError => AbuseListReadFailed,
        AbuseNewAddressesFailure.NoDownload or AbuseNewAddressesFailure.NotKnown => AbuseNewAddressesNotKnown,
        _ => Generic,
    };

    /// <summary>A network's countries cell: IPinfo's top three by addresses, then how many more ("SG 120, US 40, DE 3, +3").</summary>
    public static string AbuseNetworkCountries(IReadOnlyList<AbuseCountryCount> countries)
    {
        ArgumentNullException.ThrowIfNull(countries);
        if (countries.Count == 0)
            return Missing;

        var top = countries.OrderByDescending(c => c.Addresses).ThenBy(c => c.CountryCode, StringComparer.Ordinal)
            .Take(3).Select(c => c.CountryCode + " " + Number(c.Addresses));
        string line = string.Join(", ", top);
        return countries.Count > 3 ? line + ", +" + Number(countries.Count - 3) : line;
    }

    // A future UTC time on the AbuseIPDB page (a retry or the next download), rounded up to the next whole minute so
    // the time named is never before it
    private static string UtcMinute(DateTimeOffset at) =>
        UpToMinute(at.UtcDateTime).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    // A past UTC time on the AbuseIPDB page (the last download), cut to its whole minute so the time named is never after it
    private static string UtcMinuteDown(DateTimeOffset at)
    {
        DateTime utc = at.UtcDateTime;
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMinute)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }

    private static string FileNames(IReadOnlyList<string> paths) => NameList(paths.Select(Path.GetFileName));

    // Names in a line: "X", "X and Y", or "X, Y and Z" for three or more, with no comma before the "and"
    private static string NameList(IEnumerable<string?> names)
    {
        List<string?> list = names.ToList();
        return list.Count <= 2
            ? string.Join(" and ", list)
            : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Count(long count, string singular, string plural) =>
        count == 1 ? "1 " + singular : Number(count) + " " + plural;

    private static string AsnName(string asName) => string.IsNullOrWhiteSpace(asName) ? "" : Dash + asName;
}
