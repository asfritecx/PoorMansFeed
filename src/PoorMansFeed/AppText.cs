using System.Globalization;
using System.Numerics;
using PoorMansFeed.Core;
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
    public const string SettingsTooltip = "Settings";
    public const string ResetTokenTooltip = "Reset API token";
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
    public const string NoTokenCheck = "An API token is needed to check for updates. Choose Reset API token to enter one.";
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
    public const string TokenRejected = "The API token wasn't accepted. Choose Reset API token to enter a new one.";
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

    // Results
    public const string SummaryInitial = "Search results will appear below here.";
    public const string Searching = "Searching" + Ellipsis;
    public const string ResultsInitial = "Results appear here after a search.";
    public const string ResultsNoDataset = "Download the dataset to start searching.";
    public const string ResultsInvalid = "Enter an ASN such as AS15169, or an IPv4 or IPv6 address.";
    public const string ResultsIPv6Disabled = "IPv6 is turned off in Settings.";
    public const string SearchFailed = "The search couldn't be completed. Please try again or contact the Infra Team.";
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
    public const string CopyFailed = "The results couldn't be copied.";
    public const string FolderOpenFailed = "The folder couldn't be opened.";

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

    // Reset token confirmation
    public const string ResetTokenTitle = "Reset API token?";
    public const string ResetTokenBody = "The saved API token is removed from this computer and you're asked for a new one. The dataset is kept. This cannot be undone.";
    public const string ResetTokenButton = "Reset token";

    // Settings dialog
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
    public const string ChooseAddressFamily = "Choose at least one address family.";
    public const string EnterBothPrefixes = "Enter both prefix lengths.";
    public const string SettingsSaveFailed = "The settings couldn't be saved. Please try again or contact the Infra Team.";

    // Close while the dataset is updating
    public const string CloseBusyTitle = "Cancel the update?";
    public const string CloseBusyBody = "The dataset is still updating. Closing " + AppInfo.Name + " cancels the update and then closes the application. The existing dataset is kept.";
    public const string CloseBusyButton = "Cancel and close";

    // Help dialog
    public const string HelpTokenHeading = "1. API token";
    public const string HelpTokenText = "Enter your IPinfo API token when asked. Use Reset API token to change it.";
    public const string HelpDatasetHeading = "2. Dataset";
    public const string HelpSearchHeading = "3. Search";
    public const string HelpExportHeading = "4. Export";
    public const string HelpExportText = "Export writes every network for the ASN to the export folder, with a Fortigate threat feed if that is turned on in Settings. Large ranges are left out unless you tick them. Google Cloud Lookup, Azure Lookup and AWS Lookup, in the list on the left, write a Fortigate feed of the Google Cloud ranges for one region, of the Azure ranges for one service tag, or of the AWS ranges for one service and region.";

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
        $"Enter an ASN such as AS15169 or an IP address, then choose Search. The list shows up to {MainViewModel.ResultLimit} networks.";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Count(long count, string singular, string plural) =>
        count == 1 ? "1 " + singular : Number(count) + " " + plural;

    private static string AsnName(string asName) => string.IsNullOrWhiteSpace(asName) ? "" : Dash + asName;
}
