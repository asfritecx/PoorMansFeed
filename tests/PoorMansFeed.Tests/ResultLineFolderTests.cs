using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed.Tests;

// A feed file is named with its type's folder in every result line, joined by this platform's separator
public sealed class ResultLineFolderTests
{
    private static readonly string Exports = Path.Combine(Path.GetTempPath(), "exports");
    private const string Dash = AppText.Dash;

    private static string Fortigate(string name) => Path.Combine(Exports, "fortigate", name);

    private static string ModSecurity(string name) => Path.Combine(Exports, "modsecurity", name);

    private static string Named(string folder, string name) => folder + Path.DirectorySeparatorChar + name;

    [Fact]
    public void A_Failed_Cloud_Write_Names_Each_File_With_Its_Own_Folder()
    {
        string line = AppText.CloudWriteFailedLine(ModSecurity("AWS_EC2_all_ipv4.txt"),
            [Fortigate("AWS_EC2_all_ipv4.txt"), Fortigate("AWS_EC2_all_ipv6.txt")], [ModSecurity("AWS_EC2_all_ipv4.txt")],
            unsaved: false);

        Assert.Equal(Named("modsecurity", "AWS_EC2_all_ipv4.txt") + " couldn't be written. "
            + Named("fortigate", "AWS_EC2_all_ipv4.txt") + " and " + Named("fortigate", "AWS_EC2_all_ipv6.txt")
            + " were written before the failure. The old " + Named("modsecurity", "AWS_EC2_all_ipv4.txt")
            + " was left as it was. Please try again or contact the Infra Team.", line);
    }

    [Fact]
    public void A_Path_With_No_Folder_Is_Named_Bare()
    {
        Assert.Equal("a.txt couldn't be written. Please try again or contact the Infra Team.",
            AppText.CloudWriteFailedLine("a.txt", [], null, unsaved: false));
        Assert.Equal(Dash + "the old " + Named("fortigate", "a.txt") + " and b.txt were left as they were",
            AppText.OldFeedFilesLeft([Fortigate("a.txt"), "b.txt"]));
    }

    [Fact]
    public void The_Export_Names_A_ModSecurity_Copy_With_Its_Folder_And_The_Report_Bare()
    {
        string report = Path.Combine(Exports, "AS15169.txt");

        Assert.Equal(Named("modsecurity", "AS15169_ipv4.txt") + " couldn't be written. AS15169.txt and "
            + Named("fortigate", "AS15169_ipv4.txt") + " were written before the failure. Please try again or contact "
            + "the Infra Team.",
            AppText.ExportFileFailed(ModSecurity("AS15169_ipv4.txt"), [report, Fortigate("AS15169_ipv4.txt")], null, false));
    }

    // The order tells the report from a feed, so an export folder called modsecurity doesn't make the report a feed
    [Fact]
    public void The_Report_Is_Named_Bare_In_An_Export_Folder_Called_ModSecurity()
    {
        string folder = Path.Combine(Path.GetTempPath(), "modsecurity");
        string report = Path.Combine(folder, "AS15169.txt");
        string combined = Path.Combine(folder, "modsecurity", "AS15169.txt");

        Assert.Equal("AS15169.txt couldn't be written. The old AS15169.txt and " + Named("modsecurity", "AS15169.txt")
            + " were left as they were. Please try again or contact the Infra Team.",
            AppText.ExportFileFailed(report, [], [report, combined], false));
        Assert.Equal(Named("modsecurity", "AS15169.txt") + " couldn't be written. AS15169.txt was written before the "
            + "failure. Please try again or contact the Infra Team.",
            AppText.ExportFileFailed(combined, [report], null, false));
    }

    [Fact]
    public void A_Written_AbuseIpDb_Feed_Names_The_Folders_It_Wrote_To()
    {
        AbuseFeedWriteResult both = new(AbuseFeedWriteFailure.None,
            [Fortigate("AbuseIPDB_rolling_ipv4.txt"), Fortigate("AbuseIPDB_rolling_ipv6.txt"),
             ModSecurity("AbuseIPDB_rolling_ipv4.txt"), ModSecurity("AbuseIPDB_rolling_ipv6.txt")],
            10, 0, 0, 0, [Fortigate("AbuseIPDB_rolling_ipv6.txt"), ModSecurity("AbuseIPDB_rolling_ipv6.txt")]);
        AbuseFeedWriteResult modSecurityOnly = new(AbuseFeedWriteFailure.None, [ModSecurity("AbuseIPDB_rolling_ipv4.txt")],
            1, 0, 0, 0, []);
        AbuseFeedWriteResult none = new(AbuseFeedWriteFailure.None, [], 0, 0, 0, 0, []);

        Assert.Equal("Feed written: 10 IPv4 and 0 IPv6 addresses to the fortigate and modsecurity folders of " + Exports
            + Dash + Named("fortigate", "AbuseIPDB_rolling_ipv6.txt") + " and "
            + Named("modsecurity", "AbuseIPDB_rolling_ipv6.txt") + " were emptied", AppText.AbuseFeedWritten(both, Exports));
        Assert.Equal("Feed written: 1 IPv4 and 0 IPv6 addresses to the modsecurity folder of " + Exports,
            AppText.AbuseFeedWritten(modSecurityOnly, Exports));
        Assert.Equal("Feed written: 0 IPv4 and 0 IPv6 addresses to " + Exports, AppText.AbuseFeedWritten(none, Exports));
    }

    [Fact]
    public void Empty_Feed_Names_Both_Folders()
    {
        AbuseFeedWriteResult emptied = new(AbuseFeedWriteFailure.None,
            [Fortigate("AbuseIPDB_rolling_ipv4.txt"), ModSecurity("AbuseIPDB_rolling_ipv4.txt")], 0, 0, 0, 0,
            [Fortigate("AbuseIPDB_rolling_ipv4.txt"), ModSecurity("AbuseIPDB_rolling_ipv4.txt")]);

        Assert.Equal("Feed emptied: " + Named("fortigate", "AbuseIPDB_rolling_ipv4.txt") + " and "
            + Named("modsecurity", "AbuseIPDB_rolling_ipv4.txt") + " in " + Exports, AppText.AbuseFeedEmptied(emptied, Exports));
        Assert.Equal("Nothing was emptied" + Dash + "there's no AbuseIPDB feed file in the fortigate or modsecurity folder of "
            + Exports, AppText.AbuseFeedEmptyFailureLine(new(AbuseFeedWriteFailure.NothingToWrite, [], 0, 0, 0, 0, []), Exports));
        Assert.Equal("Each AbuseIPDB feed file in the export folder's fortigate and modsecurity folders, whatever is ticked, "
            + "is rewritten with no addresses, so a FortiGate or ModSecurity server matches none of them once it reads the "
            + "file again. The AbuseIPDB list is kept, and the next Write feed fills the files again.",
            AppText.EmptyAbuseFeedBody);
    }

    [Fact]
    public void A_Failed_AbuseIpDb_Write_Names_A_ModSecurity_File_With_Its_Folder()
    {
        AbuseFeedWriteResult failed = new(AbuseFeedWriteFailure.WriteFailed, [Fortigate("AbuseIPDB_rolling_ipv4.txt")], 0, 0,
            0, 0, [], FailedFile: ModSecurity("AbuseIPDB_rolling_ipv4.txt"), OldFilesLeft: [ModSecurity("AbuseIPDB_rolling_ipv4.txt")]);

        Assert.Equal(Named("modsecurity", "AbuseIPDB_rolling_ipv4.txt") + " couldn't be written. "
            + Named("fortigate", "AbuseIPDB_rolling_ipv4.txt") + " was written before the failure. The old "
            + Named("modsecurity", "AbuseIPDB_rolling_ipv4.txt") + " was left as it was. Please try again or contact the "
            + "Infra Team.", AppText.AbuseFeedWriteFailureLine(failed, 30, ipv6Enabled: true));
    }

    [Fact]
    public void No_Feed_Type_Has_Its_Line_And_Takes_The_Unsaved_Note()
    {
        AbuseFeedWriteResult noType = new(AbuseFeedWriteFailure.NoFeedType, [], 0, 0, 0, 0, []);

        Assert.Equal("Nothing was written" + Dash + "no feed type is ticked in Settings",
            AppText.AbuseFeedWriteFailureLine(noType, 30, ipv6Enabled: true));
        Assert.Equal("Nothing was written" + Dash + "no feed type is ticked in Settings" + AppText.UnsavedSettingsNote,
            AppText.AbuseFeedWriteFailureLine(noType, 30, ipv6Enabled: true, unsaved: true));
        Assert.Equal(Dash + "no feed was written, because no feed type is ticked in Settings", AppText.ExportNoFeedType);
    }
}
