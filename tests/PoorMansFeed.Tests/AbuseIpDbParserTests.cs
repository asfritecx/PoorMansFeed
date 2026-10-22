using System.Net;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed.Tests;

/// <summary>
/// Reading a check answer: the numbers, date, flags and country code are checked and kept, the answer must be about
/// the address that was asked, and no free text from it is kept.
/// </summary>
public sealed class AbuseIpDbParserTests
{
    private const string Asked = "118.25.6.39";

    /// <summary>The documented sample answer, as raw JSON values in file order.</summary>
    private static readonly (string Name, string Json)[] Sample =
    [
        ("ipAddress", "\"118.25.6.39\""),
        ("isPublic", "true"),
        ("ipVersion", "4"),
        ("isWhitelisted", "false"),
        ("abuseConfidenceScore", "100"),
        ("countryCode", "\"CN\""),
        ("usageType", "\"Data Center/Web Hosting/Transit\""),
        ("isp", "\"Tencent Cloud Computing (Beijing) Co. Ltd\""),
        ("domain", "\"tencent.com\""),
        ("hostnames", "[]"),
        ("isTor", "false"),
        ("totalReports", "1"),
        ("numDistinctUsers", "1"),
        ("lastReportedAt", "\"2018-12-20T20:55:14+00:00\""),
    ];

    [Fact]
    public void The_documented_sample_gives_every_field()
    {
        AbuseCheck? check = Parse(Body());

        Assert.NotNull(check);
        Assert.Equal(new AbuseCheck(100, 1, 1, new DateTimeOffset(2018, 12, 20, 20, 55, 14, TimeSpan.Zero), false,
                                    false, "CN"), check);
        Assert.Equal(TimeSpan.Zero, check.LastReportedAt!.Value.Offset);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        AbuseCheck? check = Parse(Body(("reports", "[{\"comment\":\"x\"}]"), ("countryName", "\"China\"")));

        Assert.NotNull(check);
        Assert.Equal(100, check.Score);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("100", 100)]
    [InlineData("42", 42)]
    public void A_score_from_0_to_100_is_kept(string json, int expected)
    {
        Assert.Equal(expected, Parse(Body(("abuseConfidenceScore", json)))?.Score);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("87.5")]
    [InlineData("\"87\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("10000000000")]
    public void A_score_out_of_range_or_not_an_integer_is_refused(string json)
    {
        Assert.Null(Parse(Body(("abuseConfidenceScore", json))));
    }

    [Fact]
    public void A_missing_score_is_refused()
    {
        Assert.Null(Parse(Body(("abuseConfidenceScore", null))));
    }

    [Fact]
    public void Missing_counts_give_0()
    {
        AbuseCheck? check = Parse(Body(("totalReports", null), ("numDistinctUsers", null)));

        Assert.NotNull(check);
        Assert.Equal(0, check.TotalReports);
        Assert.Equal(0, check.DistinctUsers);
    }

    [Fact]
    public void Null_counts_give_0()
    {
        AbuseCheck? check = Parse(Body(("totalReports", "null"), ("numDistinctUsers", "null")));

        Assert.NotNull(check);
        Assert.Equal(0, check.TotalReports);
        Assert.Equal(0, check.DistinctUsers);
    }

    [Fact]
    public void Counts_are_kept_apart()
    {
        AbuseCheck? check = Parse(Body(("totalReports", "57"), ("numDistinctUsers", "12")));

        Assert.NotNull(check);
        Assert.Equal(57, check.TotalReports);
        Assert.Equal(12, check.DistinctUsers);
    }

    [Theory]
    [InlineData("totalReports", "-1")]
    [InlineData("totalReports", "1.5")]
    [InlineData("totalReports", "\"3\"")]
    [InlineData("numDistinctUsers", "-1")]
    [InlineData("numDistinctUsers", "1.5")]
    [InlineData("numDistinctUsers", "\"3\"")]
    public void A_negative_or_non_integer_count_is_refused(string name, string json)
    {
        Assert.Null(Parse(Body((name, json))));
    }

    [Fact]
    public void A_null_last_report_gives_null()
    {
        AbuseCheck? check = Parse(Body(("lastReportedAt", "null")));

        Assert.NotNull(check);
        Assert.Null(check.LastReportedAt);
    }

    [Fact]
    public void A_missing_last_report_gives_null()
    {
        AbuseCheck? check = Parse(Body(("lastReportedAt", null)));

        Assert.NotNull(check);
        Assert.Null(check.LastReportedAt);
    }

    /// <summary>
    /// A time with no offset is UTC exactly as written, on any machine. The second case falls in the hour New York
    /// skips in spring, so reading it as local time there would move or refuse it.
    /// </summary>
    [Theory]
    [InlineData("2018-12-20T20:55:14", 2018, 12, 20, 20, 55, 14)]
    [InlineData("2023-03-12T02:30:00", 2023, 3, 12, 2, 30, 0)]
    public void A_last_report_without_an_offset_is_read_as_UTC_as_written(string text, int year, int month, int day,
                                                                          int hour, int minute, int second)
    {
        AbuseCheck? check = Parse(Body(("lastReportedAt", $"\"{text}\"")));

        Assert.NotNull(check);
        DateTimeOffset reported = check.LastReportedAt!.Value;
        Assert.Equal(TimeSpan.Zero, reported.Offset);
        Assert.Equal(new DateTime(year, month, day, hour, minute, second), reported.DateTime);
    }

    [Theory]
    [InlineData("2018-12-20T20:55:14Z", 20)]
    [InlineData("2018-12-20T20:55:14-05:00", 1)]
    public void A_last_report_with_Z_or_a_negative_offset_is_kept_as_UTC(string text, int utcHour)
    {
        AbuseCheck? check = Parse(Body(("lastReportedAt", $"\"{text}\"")));

        Assert.NotNull(check);
        DateTimeOffset reported = check.LastReportedAt!.Value;
        Assert.Equal(TimeSpan.Zero, reported.Offset);
        Assert.Equal(utcHour, reported.Hour);
    }

    [Fact]
    public void A_last_report_with_another_offset_is_kept_as_UTC()
    {
        AbuseCheck? check = Parse(Body(("lastReportedAt", "\"2023-03-01T10:15:00+01:00\"")));

        Assert.NotNull(check);
        DateTimeOffset reported = check.LastReportedAt!.Value;
        Assert.Equal(TimeSpan.Zero, reported.Offset);
        Assert.Equal(new DateTime(2023, 3, 1, 9, 15, 0), reported.DateTime);
    }

    /// <summary>
    /// The earliest date, which a reader that applies the machine's offset first refuses on a machine east of UTC.
    /// </summary>
    [Fact]
    public void The_earliest_last_report_without_an_offset_is_read_on_any_machine()
    {
        AbuseCheck? check = Parse(Body(("lastReportedAt", "\"0001-01-01T00:00:00\"")));

        Assert.NotNull(check);
        Assert.Equal(new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero), check.LastReportedAt);
    }

    /// <summary>
    /// A string with bytes that aren't UTF-8 is refused, although the document as a whole parses. The bytes are spliced
    /// in where the sample holds "@@", so this source stays ASCII.
    /// </summary>
    [Theory]
    [InlineData("ipAddress", "\"118.25.6.39@@\"")]
    [InlineData("countryCode", "\"C@@\"")]
    [InlineData("lastReportedAt", "\"2018-12-20T20:55:14@@\"")]
    public void A_string_that_is_not_UTF8_is_refused(string name, string json)
    {
        Assert.Null(Parse(WithInvalidUtf8(Body((name, json)))));
    }

    [Theory]
    [InlineData("\"yesterday\"")]
    [InlineData("\"\"")]
    [InlineData("1545973200")]
    [InlineData("true")]
    public void A_last_report_that_is_not_a_date_is_refused(string json)
    {
        Assert.Null(Parse(Body(("lastReportedAt", json))));
    }

    [Fact]
    public void A_two_capital_letter_country_code_is_kept()
    {
        Assert.Equal("US", Parse(Body(("countryCode", "\"US\"")))?.CountryCode);
    }

    [Theory]
    [InlineData("\"us\"")]
    [InlineData("\"Us\"")]
    [InlineData("\"USA\"")]
    [InlineData("\"U\"")]
    [InlineData("\"\"")]
    [InlineData("\"U1\"")]
    [InlineData("\"\\u00C9S\"")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("[\"US\"]")]
    public void Any_other_country_code_is_dropped_without_failing(string json)
    {
        AbuseCheck? check = Parse(Body(("countryCode", json)));

        Assert.NotNull(check);
        Assert.Null(check.CountryCode);
    }

    [Fact]
    public void A_missing_country_code_gives_null()
    {
        AbuseCheck? check = Parse(Body(("countryCode", null)));

        Assert.NotNull(check);
        Assert.Null(check.CountryCode);
    }

    [Theory]
    [InlineData("isWhitelisted", "true", true)]
    [InlineData("isWhitelisted", "false", false)]
    [InlineData("isWhitelisted", "null", null)]
    [InlineData("isTor", "true", true)]
    [InlineData("isTor", "false", false)]
    [InlineData("isTor", "null", null)]
    public void A_flag_is_true_false_or_null(string name, string json, bool? expected)
    {
        AbuseCheck? check = Parse(Body((name, json)));

        Assert.NotNull(check);
        Assert.Equal(expected, name == "isTor" ? check.IsTor : check.IsWhitelisted);
    }

    [Theory]
    [InlineData("isWhitelisted")]
    [InlineData("isTor")]
    public void A_missing_flag_gives_null(string name)
    {
        AbuseCheck? check = Parse(Body((name, null)));

        Assert.NotNull(check);
        Assert.Null(name == "isTor" ? check.IsTor : check.IsWhitelisted);
    }

    [Theory]
    [InlineData("isWhitelisted", "\"true\"")]
    [InlineData("isWhitelisted", "1")]
    [InlineData("isTor", "\"true\"")]
    [InlineData("isTor", "1")]
    public void A_flag_of_another_type_is_refused(string name, string json)
    {
        Assert.Null(Parse(Body((name, json))));
    }

    [Theory]
    [InlineData("8.8.8.8", "\"8.8.8.8\"")]
    [InlineData("2001:4860::8888", "\"2001:4860::8888\"")]
    [InlineData("2001:4860::8888", "\"2001:4860:0:0:0:0:0:8888\"")]
    public void The_asked_address_is_accepted_in_any_plain_form(string asked, string json)
    {
        Assert.NotNull(AbuseIpDbParser.TryParseCheck(Body(("ipAddress", json)), Address(asked)));
    }

    [Theory]
    [InlineData(Asked, "\"118.25.6.40\"")]
    [InlineData(Asked, "\"8.8.8.8\"")]
    [InlineData(Asked, "\"not an address\"")]
    [InlineData(Asked, "\"\"")]
    [InlineData(Asked, "\" 118.25.6.39\"")]
    [InlineData(Asked, "null")]
    [InlineData(Asked, "1981482535")]
    [InlineData("8.8.8.8", "\"008.008.008.008\"")]
    [InlineData("8.8.8.8", "\"::ffff:8.8.8.8\"")]
    [InlineData("2001:4860::808:808", "\"8.8.8.8\"")]
    [InlineData("2001:4860::8888", "\"2001:4860::8889\"")]
    public void An_answer_about_another_address_is_refused(string asked, string json)
    {
        Assert.Null(AbuseIpDbParser.TryParseCheck(Body(("ipAddress", json)), Address(asked)));
    }

    [Fact]
    public void An_answer_without_an_address_is_refused()
    {
        Assert.Null(Parse(Body(("ipAddress", null))));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":\"x\"}")]
    [InlineData("[{\"data\":{}}]")]
    [InlineData("\"data\"")]
    [InlineData("{\"errors\":[{\"detail\":\"x\",\"status\":401}]}")]
    [InlineData("{\"data\":{")]
    [InlineData("not json")]
    [InlineData("")]
    public void A_body_that_is_not_a_check_answer_is_refused(string body)
    {
        Assert.Null(Parse(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void The_data_object_is_required_at_the_top()
    {
        string data = Encoding.UTF8.GetString(Body());
        string inner = data["{\"data\":".Length..^1];

        Assert.Null(Parse(Encoding.UTF8.GetBytes(inner)));
    }

    [Fact]
    public void No_free_text_from_the_answer_is_kept()
    {
        const string Sentinel = "SENTINEL";
        AbuseCheck? check = Parse(Body(
            ("isp", "\"SENTINEL-isp\""),
            ("domain", "\"SENTINEL-domain\""),
            ("usageType", "\"SENTINEL-usage\""),
            ("hostnames", "[\"SENTINEL-host-1\",\"SENTINEL-host-2\"]")));

        Assert.NotNull(check);
        foreach (var property in typeof(AbuseCheck).GetProperties())
        {
            string text = Convert.ToString(property.GetValue(check), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            Assert.DoesNotContain(Sentinel, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(Sentinel, check.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------- The blacklist ----------

    private static readonly DateTimeOffset DownloadedAt = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Generated = new(2026, 10, 19, 7, 0, 0, TimeSpan.Zero);
    private const string GeneratedJson = "\"2026-10-19T07:00:00+00:00\"";
    private const string Reported = "\"2026-10-18T10:00:00+00:00\"";

    [Fact]
    public void A_blacklist_gives_its_time_and_entries_of_both_families_in_file_order()
    {
        Blacklist list = ParseList(List(
            Entry("8.8.8.8", score: 100, lastReportedAt: Reported, countryCode: "\"US\""),
            Entry("2001:4860:4860::8888", score: 75, lastReportedAt: null, countryCode: null)))!;

        Assert.NotNull(list);
        Assert.Equal(Generated, list.GeneratedAt);
        Assert.Equal((0, 0, 0), (list.Unreadable, list.NotPublic, list.Duplicates));
        Assert.Collection(list.Entries,
            entry =>
            {
                Assert.Equal("8.8.8.8", entry.Address.Text);
                Assert.Equal(4, entry.Address.Bytes.Length);
                Assert.Equal(100, entry.Score);
                Assert.Equal(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero), entry.LastReportedAt);
                Assert.Equal("US", entry.CountryCode);
            },
            entry =>
            {
                Assert.Equal("2001:4860:4860::8888", entry.Address.Text);
                Assert.Equal(16, entry.Address.Bytes.Length);
                Assert.Equal(75, entry.Score);
                Assert.Null(entry.LastReportedAt);
                Assert.Null(entry.CountryCode);
            });
    }

    [Fact]
    public void An_IPv4_mapped_address_is_kept_as_4_bytes()
    {
        Blacklist list = ParseList(List(Entry("::ffff:8.8.8.8")))!;

        BlacklistEntry entry = Assert.Single(list.Entries);
        Assert.Equal(new byte[] { 8, 8, 8, 8 }, entry.Address.Bytes);
        Assert.Equal("8.8.8.8", entry.Address.Text);
    }

    [Theory]
    [InlineData("\"8.8.8.0/24\"")]
    [InlineData("\"fe80::1%eth0\"")]
    [InlineData("\"[2001:4860:4860::8888]\"")]
    [InlineData("\"8.8.8.8:80\"")]
    [InlineData("\"08.8.8.8\"")]
    [InlineData("\"8.8.8\"")]
    [InlineData("\" 8.8.8.8\"")]
    [InlineData("\"8.8.8.8 \"")]
    [InlineData("\"\"")]
    [InlineData("134744072")]
    [InlineData("null")]
    public void An_address_that_cannot_be_read_counts_as_unreadable(string ipAddress)
    {
        Blacklist list = ParseList(List(RawEntry(ipAddress), Entry("1.1.1.1")))!;

        Assert.Equal((1, 0, 0), (list.Unreadable, list.NotPublic, list.Duplicates));
        Assert.Equal("1.1.1.1", Assert.Single(list.Entries).Address.Text);
    }

    [Theory]
    [InlineData("8")]
    [InlineData("\"8.8.8.8\"")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"abuseConfidenceScore\":100}")]
    public void An_element_that_is_not_an_entry_counts_as_unreadable(string element)
    {
        Blacklist list = ParseList(List(element, Entry("1.1.1.1")))!;

        Assert.Equal((1, 0, 0), (list.Unreadable, list.NotPublic, list.Duplicates));
        Assert.Single(list.Entries);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.64.0.0")]
    [InlineData("100.127.255.255")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("2002::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("2001:db8::1")]
    public void An_address_that_is_not_public_counts_as_not_public(string address)
    {
        Blacklist list = ParseList(List(Entry(address), Entry("1.1.1.1")))!;

        Assert.Equal((0, 1, 0), (list.Unreadable, list.NotPublic, list.Duplicates));
        Assert.Equal("1.1.1.1", Assert.Single(list.Entries).Address.Text);
    }

    [Theory]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("2606:4700:4700::1111")]
    public void The_public_edges_are_kept(string address)
    {
        Blacklist list = ParseList(List(Entry(address)))!;

        Assert.Equal(address, Assert.Single(list.Entries).Address.Text);
        Assert.Equal(0, list.NotPublic);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("50.5")]
    [InlineData("\"50\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("2147483648")]
    public void A_score_missing_or_out_of_range_counts_as_unreadable(string? score)
    {
        Blacklist list = ParseList(List(RawEntry("\"8.8.8.8\"", score), Entry("1.1.1.1")))!;

        Assert.Equal((1, 0, 0), (list.Unreadable, list.NotPublic, list.Duplicates));
        Assert.Single(list.Entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void The_score_edges_are_kept(int score)
    {
        Blacklist list = ParseList(List(Entry("8.8.8.8", score: score)))!;

        Assert.Equal(score, Assert.Single(list.Entries).Score);
    }

    [Theory]
    [InlineData("\"soon\"")]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("\"2026-13-01T00:00:00+00:00\"")]
    public void A_last_report_that_is_not_a_date_counts_as_unreadable(string lastReportedAt)
    {
        Blacklist list = ParseList(List(Entry("8.8.8.8", lastReportedAt: lastReportedAt), Entry("1.1.1.1")))!;

        Assert.Equal((1, 0, 0), (list.Unreadable, list.NotPublic, list.Duplicates));
        Assert.Single(list.Entries);
    }

    [Theory]
    [InlineData("\"2026-10-18T22:30:00+08:00\"")]
    [InlineData("\"2026-10-18T14:30:00Z\"")]
    [InlineData("\"2026-10-18T14:30:00\"")]
    public void A_last_report_with_or_without_an_offset_is_kept_as_UTC(string lastReportedAt)
    {
        Blacklist list = ParseList(List(Entry("8.8.8.8", lastReportedAt: lastReportedAt)))!;

        DateTimeOffset? at = Assert.Single(list.Entries).LastReportedAt;
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 14, 30, 0, TimeSpan.Zero), at);
        Assert.Equal(TimeSpan.Zero, at!.Value.Offset);
    }

    [Theory]
    [InlineData("\"us\"")]
    [InlineData("\"USA\"")]
    [InlineData("\"U1\"")]
    [InlineData("\"\"")]
    [InlineData("5")]
    [InlineData("null")]
    public void A_country_code_that_is_not_two_capitals_is_dropped_and_the_entry_kept(string countryCode)
    {
        Blacklist list = ParseList(List(Entry("8.8.8.8", countryCode: countryCode)))!;

        Assert.Null(Assert.Single(list.Entries).CountryCode);
        Assert.Equal(0, list.Unreadable);
    }

    [Theory]
    [InlineData("\"2026-10-19T15:00:00+08:00\"")]
    [InlineData("\"2026-10-19T07:00:00Z\"")]
    [InlineData("\"2026-10-19T07:00:00\"")]
    public void The_generation_time_with_or_without_an_offset_is_kept_as_UTC(string generatedAt)
    {
        Blacklist list = ParseList(ListAt(generatedAt, Entry("8.8.8.8")))!;

        Assert.Equal(Generated, list.GeneratedAt);
        Assert.Equal(TimeSpan.Zero, list.GeneratedAt.Offset);
    }

    [Theory]
    [InlineData("{\"data\":[ENTRY]}")]
    [InlineData("{\"meta\":5,\"data\":[ENTRY]}")]
    [InlineData("{\"meta\":{},\"data\":[ENTRY]}")]
    [InlineData("{\"meta\":{\"generatedAt\":null},\"data\":[ENTRY]}")]
    [InlineData("{\"meta\":{\"generatedAt\":5},\"data\":[ENTRY]}")]
    [InlineData("{\"meta\":{\"generatedAt\":\"soon\"},\"data\":[ENTRY]}")]
    public void A_missing_or_unreadable_generation_time_gives_the_download_time(string body)
    {
        Blacklist list = ParseList(body.Replace("ENTRY", Entry("8.8.8.8"), StringComparison.Ordinal))!;

        Assert.Equal(DownloadedAt, list.GeneratedAt);
        Assert.Single(list.Entries);
    }

    [Fact]
    public void A_generation_time_more_than_an_hour_ahead_is_clamped()
    {
        Blacklist list = ParseList(ListAt("\"2026-10-19T09:00:01+00:00\"", Entry("8.8.8.8")))!;

        Assert.Equal(DownloadedAt.AddHours(1), list.GeneratedAt);
    }

    [Fact]
    public void A_generation_time_an_hour_ahead_is_kept()
    {
        Blacklist list = ParseList(ListAt("\"2026-10-19T09:00:00+00:00\"", Entry("8.8.8.8")))!;

        Assert.Equal(DownloadedAt.AddHours(1), list.GeneratedAt);
        list = ParseList(ListAt("\"2026-10-19T08:59:00+00:00\"", Entry("8.8.8.8")))!;
        Assert.Equal(DownloadedAt.AddMinutes(59), list.GeneratedAt);
    }

    [Fact]
    public void The_latest_report_wins_a_duplicate_whatever_the_score_or_order()
    {
        Blacklist list = ParseList(List(
            Entry("8.8.8.8", score: 100, lastReportedAt: "\"2026-10-17T00:00:00+00:00\"", countryCode: "\"US\""),
            Entry("1.1.1.1"),
            Entry("8.8.8.8", score: 60, lastReportedAt: "\"2026-10-18T00:00:00+00:00\"", countryCode: "\"DE\""),
            Entry("8.8.8.8", score: 90, lastReportedAt: null, countryCode: "\"FR\"")))!;

        Assert.Equal(2, list.Duplicates);
        Assert.Equal(new[] { "8.8.8.8", "1.1.1.1" }, list.Entries.Select(entry => entry.Address.Text));
        BlacklistEntry winner = list.Entries[0];
        Assert.Equal((60, "DE"), (winner.Score, winner.CountryCode));
    }

    [Fact]
    public void A_dated_report_beats_one_with_no_date()
    {
        Blacklist list = ParseList(List(
            Entry("8.8.8.8", score: 100, lastReportedAt: "null", countryCode: "\"US\""),
            Entry("8.8.8.8", score: 50, lastReportedAt: Reported, countryCode: "\"DE\"")))!;

        Assert.Equal("DE", Assert.Single(list.Entries).CountryCode);
        Assert.Equal(1, list.Duplicates);
    }

    [Fact]
    public void With_the_same_report_time_the_higher_score_wins()
    {
        Blacklist list = ParseList(List(
            Entry("8.8.8.8", score: 50, countryCode: "\"US\""),
            Entry("8.8.8.8", score: 80, countryCode: "\"DE\""),
            Entry("8.8.8.8", score: 70, countryCode: "\"FR\"")))!;

        Assert.Equal((80, "DE"), (Assert.Single(list.Entries).Score, list.Entries[0].CountryCode));
        Assert.Equal(2, list.Duplicates);
    }

    [Fact]
    public void With_everything_equal_the_first_in_the_file_wins()
    {
        Blacklist list = ParseList(List(
            Entry("8.8.8.8", countryCode: "\"US\""),
            Entry("8.8.8.8", countryCode: "\"DE\"")))!;

        Assert.Equal("US", Assert.Single(list.Entries).CountryCode);
        Assert.Equal(1, list.Duplicates);
    }

    [Fact]
    public void Two_spellings_of_one_IPv6_address_collapse()
    {
        Blacklist list = ParseList(List(
            Entry("2001:4860:4860::8888"),
            Entry("2001:4860:4860:0:0:0:0:8888"),
            Entry("2001:4860:4860:0000::8888")))!;

        Assert.Equal("2001:4860:4860::8888", Assert.Single(list.Entries).Address.Text);
        Assert.Equal(2, list.Duplicates);
    }

    [Fact]
    public void An_IPv4_address_and_its_mapped_form_collapse()
    {
        Blacklist list = ParseList(List(
            Entry("1.2.3.4", countryCode: "\"US\""),
            Entry("::ffff:1.2.3.4", countryCode: "\"DE\"", lastReportedAt: "\"2026-10-18T11:00:00+00:00\"")))!;

        BlacklistEntry entry = Assert.Single(list.Entries);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, entry.Address.Bytes);
        Assert.Equal("DE", entry.CountryCode);
        Assert.Equal(1, list.Duplicates);
    }

    [Theory]
    [InlineData(10, 20, true)]
    [InlineData(11, 1100, true)]
    [InlineData(11, 1099, false)]
    [InlineData(10, 11, true)]
    [InlineData(11, 11, false)]
    public void Unreadable_entries_refuse_the_list_only_past_both_limits(int unreadable, int total, bool accepted)
    {
        var entries = Enumerable.Range(0, total)
            .Select(index => index < unreadable ? RawEntry("\"bad\"") : Entry(PublicIPv4(index)));

        Blacklist? list = ParseList(List(entries.ToArray()));

        if (accepted)
        {
            Assert.NotNull(list);
            Assert.Equal(unreadable, list.Unreadable);
            Assert.Equal(total - unreadable, list.Entries.Count);
        }
        else
        {
            Assert.Null(list);
        }
    }

    /// <summary>A list from which no entry is kept is refused, however few entries it has and whatever left them out.</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(10, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 3)]
    [InlineData(2, 2)]
    public void A_list_with_no_entry_kept_gives_null(int unreadable, int notPublic)
    {
        var entries = Enumerable.Range(0, unreadable).Select(_ => RawEntry("\"bad\""))
            .Concat(Enumerable.Range(0, notPublic).Select(index => Entry($"10.0.0.{index + 1}")));

        Assert.Null(ParseList(List(entries.ToArray())));
    }

    [Fact]
    public void Not_public_entries_never_refuse_a_list()
    {
        var entries = Enumerable.Range(0, 1000).Select(index => Entry($"10.0.{index / 256}.{index % 256}"))
                                .Append(Entry("8.8.8.8"));

        Blacklist list = ParseList(List(entries.ToArray()))!;

        Assert.NotNull(list);
        Assert.Equal((0, 1000), (list.Unreadable, list.NotPublic));
        Assert.Single(list.Entries);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"meta\":{\"generatedAt\":\"2026-10-19T07:00:00+00:00\"},\"data\":[]}")]
    public void A_body_that_is_not_a_blacklist_gives_null(string body)
    {
        Assert.Null(ParseList(body));
    }

    [Fact]
    public void A_string_that_is_not_UTF8_gives_null()
    {
        byte[] body = Encoding.UTF8.GetBytes(List(Entry("1.1.1.1"), RawEntry("\"8.8.8.@@\"")));

        Assert.Null(AbuseIpDbParser.TryParseBlacklist(WithInvalidUtf8(body), DownloadedAt));
    }

    private static Blacklist? ParseList(string body) =>
        AbuseIpDbParser.TryParseBlacklist(Encoding.UTF8.GetBytes(body), DownloadedAt);

    private static string List(params string[] entries) => ListAt(GeneratedJson, entries);

    private static string ListAt(string generatedAt, params string[] entries) =>
        "{\"meta\":{\"generatedAt\":" + generatedAt + "},\"data\":[" + string.Join(",", entries) + "]}";

    /// <summary>One entry; a null value leaves that field out.</summary>
    private static string Entry(string address, int score = 100, string? lastReportedAt = Reported, string? countryCode = "\"US\"") =>
        RawEntry("\"" + address + "\"", score.ToString(System.Globalization.CultureInfo.InvariantCulture), lastReportedAt, countryCode);

    private static string RawEntry(string ipAddress, string? score = "100", string? lastReportedAt = Reported,
                                   string? countryCode = "\"US\"")
    {
        var fields = new List<string> { "\"ipAddress\":" + ipAddress };
        if (score is not null)
            fields.Add("\"abuseConfidenceScore\":" + score);
        if (lastReportedAt is not null)
            fields.Add("\"lastReportedAt\":" + lastReportedAt);
        if (countryCode is not null)
            fields.Add("\"countryCode\":" + countryCode);
        return "{" + string.Join(",", fields) + "}";
    }

    /// <summary>A distinct public IPv4 address for each index, in 8.0.0.0/8.</summary>
    private static string PublicIPv4(int index) => $"8.{index / 65536}.{index / 256 % 256}.{index % 256}";

    private static AbuseCheck? Parse(byte[] body) => AbuseIpDbParser.TryParseCheck(body, Address(Asked));

    /// <summary>The sample answer with fields replaced, added, or (a null value) left out.</summary>
    private static byte[] Body(params (string Name, string? Json)[] changes)
    {
        var fields = Sample.Select(field => (field.Name, Json: (string?)field.Json)).ToList();
        foreach ((string name, string? json) in changes)
        {
            int index = fields.FindIndex(field => field.Name == name);
            if (index >= 0)
                fields[index] = (name, json);
            else
                fields.Add((name, json));
        }

        string members = string.Join(",", fields.Where(field => field.Json is not null)
                                                .Select(field => $"\"{field.Name}\":{field.Json}"));
        return Encoding.UTF8.GetBytes("{\"data\":{" + members + "}}");
    }

    /// <summary>The body with its one "@@" replaced by the bytes 0xFF 0xFE, which no UTF-8 text holds.</summary>
    private static byte[] WithInvalidUtf8(byte[] body)
    {
        int at = body.AsSpan().IndexOf("@@"u8);
        Assert.True(at >= 0);
        byte[] spliced = [.. body];
        spliced[at] = 0xFF;
        spliced[at + 1] = 0xFE;
        return spliced;
    }

    private static PublicAddress Address(string text)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
        Assert.True(PublicAddress.TryCreate(address.GetAddressBytes(), out PublicAddress? value));
        return value;
    }
}
