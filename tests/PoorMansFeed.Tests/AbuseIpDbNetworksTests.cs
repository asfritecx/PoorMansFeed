using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

/// <summary>
/// The offending networks: the rolling list's addresses looked up in the IPinfo dataset in chunks, matched by block,
/// started again once on a dataset change, and grouped by ASN.
/// </summary>
public sealed class AbuseIpDbNetworksTests : IDisposable
{
    private const string Key = "abuseipdb-sentinel-key-0123456789";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Downloaded = new(2026, 10, 19, 7, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Generated = new(2026, 10, 19, 7, 29, 58, TimeSpan.Zero);
    private static readonly DatasetInfo Original = new(new DateOnly(2026, 10, 19), 4, "abc", Generated, false);

    // The IPv6 rebuild keeps the checksum, so only the whole record tells it apart
    private static readonly DatasetInfo Rebuilt = Original with { ImportedUtc = Now, IncludesIPv6 = true };

    private readonly InMemorySecretStore _store = new() { Secret = Encoding.UTF8.GetBytes(Key), Marker = true };
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeIpInfoService _ipinfo = new() { Dataset = Original };
    private readonly ServiceTempFolder _temp = new();
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;

    /// <summary>The blocks of each lookup call, in order.</summary>
    private readonly List<CidrBlock[]> _calls = [];

    /// <summary>What the dataset holds for a block; nothing by default.</summary>
    private Func<CidrBlock, IReadOnlyList<OverlapGroup>> _groups = _ => [];

    public AbuseIpDbNetworksTests()
    {
        // The lookup sends nothing: a request fails the test
        _handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("The lookup sent a request."));
        _http = new HttpClient(_handler);
        _ipinfo.OverlapFunc = blocks => Answer(blocks, _ipinfo.Dataset);
    }

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    private string ListPath => _temp.Paths.AbuseIpDbDatabasePath;

    // ---------- 1. Chunk sizes ----------

    [Fact]
    public async Task IPv4_goes_in_chunks_of_1000_then_IPv6_in_chunks_of_250_with_no_chunk_mixing_families()
    {
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = true };
        BlacklistEntry[] ipv4 = Enumerable.Range(0, 2001).Select(i => Entry($"8.8.{i / 256}.{i % 256}")).ToArray();
        BlacklistEntry[] ipv6 = Enumerable.Range(1, 251).Select(i => Entry($"2001:4860::{i:x}")).ToArray();
        Seed(Generated, Downloaded, [.. ipv6, .. ipv4]);

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal([1000, 1000, 1, 250, 1], _calls.Select(call => call.Length));
        Assert.Equal([false, false, false, true, true], _calls.Select(call => call[0].IsIpv6));
        Assert.All(_calls, call => Assert.All(call, block => Assert.Equal(call[0].IsIpv6, block.IsIpv6)));
        string[] asked = _calls.SelectMany(call => call).Select(block => block.Text).ToArray();
        Assert.Equal(2252, asked.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(asked.Length, asked.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("8.8.7.208/32", asked);
        Assert.Contains("2001:4860::fb/128", asked);
        Assert.Equal(2001, result.IPv4Addresses);
        Assert.Equal(251, result.IPv6Addresses);
        Assert.Equal(2252, result.NotInIpinfo);
        Assert.True(result.IncludesIPv6);
        Assert.Equal(Original, result.Dataset);
    }

    [Fact]
    public void A_block_is_the_address_as_a_32_or_a_128()
    {
        Assert.True(AbuseIpDbNetworks.TryBlockOf(Address("8.8.8.8"), out CidrBlock? ipv4));
        Assert.Equal("8.8.8.8/32", ipv4.Text);
        Assert.False(ipv4.IsIpv6);

        Assert.True(AbuseIpDbNetworks.TryBlockOf(Address("2001:4860::8888"), out CidrBlock? ipv6));
        Assert.Equal("2001:4860::8888/128", ipv6.Text);
        Assert.True(ipv6.IsIpv6);
        Assert.Equal(128, ipv6.PrefixLength);
    }

    // ---------- 2. Grouping and order ----------

    [Fact]
    public async Task Addresses_are_grouped_by_IPinfo_ASN_with_both_buckets_and_IPinfo_countries_only()
    {
        // AbuseIPDB says RU for every one; only IPinfo's countries may come out
        Seed(Generated, Downloaded,
             Entry("8.8.8.1", "RU"), Entry("8.8.8.2", "RU"), Entry("8.8.8.3", "RU"), Entry("9.9.9.9", "RU"),
             Entry("9.9.9.10", "RU"), Entry("8.8.4.4", "RU"), Entry("8.8.4.5", "RU"), Entry("2001:4860::8888", "RU"));
        var held = new Dictionary<string, IReadOnlyList<OverlapGroup>>(StringComparer.Ordinal)
        {
            ["8.8.8.1/32"] = [Group("AS15169", "Google LLC", "google.com", "US")],
            ["8.8.8.2/32"] = [Group(" as15169 ", " Google LLC ", "google.com", "US")],
            ["8.8.8.3/32"] = [Group("15169", "Google", "google.com", "NL")],
            ["9.9.9.9/32"] = [Group("AS19281", "Quad9", "quad9.net", "CH")],
            ["9.9.9.10/32"] = [Group("AS19281", "Quad9", "quad9.net", "CH"), Group("AS15169", "Google LLC", "google.com", "CH")],
            ["8.8.4.4/32"] = [Group(null, null, null, "IE")],
            ["8.8.4.5/32"] = [Group("ASX", "Unknown", null, " IE ")],
        };
        _groups = block => held.TryGetValue(block.Text, out IReadOnlyList<OverlapGroup>? groups) ? groups : [];

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal(2, result.Networks.Count);
        AbuseNetworkGroup google = result.Networks[0];
        Assert.Equal(15169u, google.Asn);
        Assert.Equal("Google LLC", google.AsName);
        Assert.Equal("google.com", google.AsDomain);
        Assert.Equal(4, google.Addresses);
        Assert.Equal([new("US", 2), new("CH", 1), new("NL", 1)], google.Countries);
        AbuseNetworkGroup quad9 = result.Networks[1];
        Assert.Equal(new AbuseNetworkGroup(19281, "Quad9", "quad9.net", 2, quad9.Countries), quad9);
        Assert.Equal([new("CH", 2)], quad9.Countries);
        Assert.Equal(2, result.NoAsn);
        Assert.Equal([new("IE", 2)], result.NoAsnCountries);
        Assert.Equal(1, result.NotInIpinfo);
        Assert.Equal(7, result.IPv4Addresses);
        Assert.Equal(1, result.IPv6Addresses);
        // One address is under two ASNs, so the buckets hold one more than were looked up
        Assert.Equal(result.IPv4Addresses + result.IPv6Addresses + 1,
                     result.Networks.Sum(network => network.Addresses) + result.NoAsn + result.NotInIpinfo);
        Assert.DoesNotContain(result.Networks.SelectMany(network => network.Countries).Concat(result.NoAsnCountries),
                              country => country.CountryCode == "RU");
        Assert.Equal(30, result.KeepDays);
        Assert.Equal(Downloaded, result.LastDownloadAt);
    }

    [Fact]
    public void Networks_sort_by_count_then_ASN_and_names_take_the_most_addresses_ties_to_the_ordinal_first()
    {
        AbuseNetworksGrouping grouping = AbuseIpDbNetworks.Group(
        [
            [Group("AS300", "Beta", "b.example", "DE")],
            [Group("AS300", "Alpha", "a.example", "AT")],
            [Group("AS200", " ", null, "FR")],
            [Group("AS100", "Gamma", "  ", "FR"), Group("AS100", "Gamma", null, "BE")],
            [Group("AS200", null, "", "FR")],
            [Group("AS-1", null, null, null), Group("AS100", "Gamma", null, null)],
        ]);

        Assert.Equal([100u, 200u, 300u], grouping.Networks.Select(network => network.Asn));
        AbuseNetworkGroup as100 = grouping.Networks[0];
        Assert.Equal(2, as100.Addresses);
        Assert.Equal("Gamma", as100.AsName);
        Assert.Null(as100.AsDomain);
        Assert.Equal([new("BE", 1), new("FR", 1)], as100.Countries);
        AbuseNetworkGroup as200 = grouping.Networks[1];
        Assert.Null(as200.AsName);
        Assert.Null(as200.AsDomain);
        Assert.Equal([new("FR", 2)], as200.Countries);
        AbuseNetworkGroup as300 = grouping.Networks[2];
        Assert.Equal("Alpha", as300.AsName);
        Assert.Equal("a.example", as300.AsDomain);
        Assert.Equal([new("AT", 1), new("DE", 1)], as300.Countries);
        Assert.Equal(0, grouping.NoAsn);
        Assert.Equal(0, grouping.MultiAsnAddresses);
        Assert.Equal(1, grouping.UnreadableAsns);
    }

    [Fact]
    public void An_address_under_two_ASNs_counts_under_each_and_one_with_no_ASN_that_reads_goes_in_the_no_ASN_bucket()
    {
        AbuseNetworksGrouping grouping = AbuseIpDbNetworks.Group(
        [
            [Group("AS1", "One", null, "US"), Group("AS2", "Two", null, "US")],
            [Group("AS1 x", null, null, "NL"), Group("", null, null, "NL"), Group(null, null, null, "SE")],
            [],
            [],
        ]);

        Assert.Equal([1u, 2u], grouping.Networks.Select(network => network.Asn));
        Assert.All(grouping.Networks, network => Assert.Equal(1, network.Addresses));
        Assert.Equal(1, grouping.MultiAsnAddresses);
        Assert.Equal(1, grouping.NoAsn);
        Assert.Equal([new("NL", 1), new("SE", 1)], grouping.NoAsnCountries);
        Assert.Equal(1, grouping.UnreadableAsns);
        Assert.Equal(2, grouping.NotInIpinfo);
    }

    [Theory]
    [InlineData("AS15169", 15169u)]
    [InlineData(" as15169 ", 15169u)]
    [InlineData("15169", 15169u)]
    [InlineData("aS0", 0u)]
    [InlineData("AS4294967295", 4294967295u)]
    public void An_ASN_reads_with_or_without_AS_in_any_case(string text, uint expected)
    {
        Assert.True(AbuseIpDbNetworks.TryParseAsn(text, out uint asn));
        Assert.Equal(expected, asn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AS")]
    [InlineData("AS-1")]
    [InlineData("AS 15169")]
    [InlineData("AS+15169")]
    [InlineData("AS4294967296")]
    [InlineData("AS15169x")]
    [InlineData("AS1.5")]
    [InlineData("ASAS1")]
    [InlineData("\u0661\u0662")]
    public void Anything_else_is_not_an_ASN(string? text)
    {
        Assert.False(AbuseIpDbNetworks.TryParseAsn(text, out _));
    }

    // ---------- 3. Matched by block ----------

    [Fact]
    public async Task An_answer_in_another_order_gives_the_same_groups()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("8.8.4.4"), Entry("9.9.9.9"));
        _groups = block => block.Text switch
        {
            "8.8.8.8/32" => [Group("AS15169", "Google LLC", "google.com", "US")],
            "9.9.9.9/32" => [Group("AS19281", "Quad9", "quad9.net", "CH")],
            _ => [],
        };
        AbuseNetworksResult inOrder = await Find();

        _ipinfo.OverlapFunc = blocks => Answer(blocks.Reverse().ToArray(), _ipinfo.Dataset);
        AbuseNetworksResult reversed = await Find();

        Assert.Equal(AbuseNetworksFailure.None, reversed.Failure);
        Assert.Equal([15169u, 19281u], reversed.Networks.Select(network => network.Asn));
        Assert.Equal(inOrder.Networks.Select(network => (network.Asn, network.AsName, network.Addresses)),
                     reversed.Networks.Select(network => (network.Asn, network.AsName, network.Addresses)));
        Assert.Equal(1, reversed.NotInIpinfo);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("extra")]
    public async Task A_missing_repeated_or_extra_block_fails_the_lookup(string mismatch)
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("8.8.4.4"));
        Assert.True(CidrBlock.TryParse("9.9.9.9/32", out CidrBlock? other));
        _ipinfo.OverlapFunc = blocks =>
        {
            OverlapResult found = Answer(blocks, _ipinfo.Dataset);
            List<NetworkOverlap> overlaps = [.. found.Overlaps];
            switch (mismatch)
            {
                case "missing": overlaps.RemoveAt(1); break;
                case "repeated": overlaps.Add(overlaps[0]); break;
                default: overlaps.Add(new NetworkOverlap(other, [])); break;
            }
            return found with { Overlaps = overlaps };
        };

        AbuseNetworksResult result = await Find();

        AssertFailure(AbuseNetworksFailure.LookupFailed, result);
        Assert.Equal(1, _ipinfo.OverlapCalls);
    }

    [Fact]
    public async Task An_IPv6_block_missing_while_IPv6_is_still_on_fails_the_lookup()
    {
        Seed(Generated, Downloaded, Entry("2001:4860::8888"));
        _ipinfo.OverlapFunc = blocks => new OverlapResult(OverlapOutcome.Found, [], _ipinfo.Dataset);

        AbuseNetworksResult result = await Find();

        AssertFailure(AbuseNetworksFailure.LookupFailed, result);
        Assert.Equal(1, _ipinfo.OverlapCalls);
    }

    // ---------- 4. No dataset and failed lookups ----------

    [Fact]
    public async Task No_dataset_is_reported_before_any_lookup()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        _ipinfo.Dataset = null;

        AbuseNetworksResult result = await Find();

        AssertFailure(AbuseNetworksFailure.NoDataset, result);
        Assert.Equal(0, _ipinfo.OverlapCalls);
        Assert.Equal(Downloaded, result.LastDownloadAt);
    }

    [Theory]
    [InlineData(OverlapOutcome.NoDataset, true, AbuseNetworksFailure.NoDataset)]
    [InlineData(OverlapOutcome.Failed, false, AbuseNetworksFailure.LookupFailed)]
    [InlineData(OverlapOutcome.Found, false, AbuseNetworksFailure.LookupFailed)]
    public async Task A_chunk_with_no_dataset_or_a_failure_or_no_dataset_label_stops_the_lookup(OverlapOutcome outcome, bool labelled,
                                                                                                 AbuseNetworksFailure expected)
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        _ipinfo.OverlapFunc = blocks => new OverlapResult(outcome, [.. Answer(blocks, null).Overlaps],
                                                          labelled ? _ipinfo.Dataset : null);

        AbuseNetworksResult result = await Find();

        AssertFailure(expected, result);
        Assert.Equal(1, _ipinfo.OverlapCalls);
    }

    [Fact]
    public async Task No_dataset_from_a_later_chunk_stops_the_lookup()
    {
        SeedIpv4(1001);
        _ipinfo.OverlapFunc = blocks =>
        {
            OverlapResult answer = Answer(blocks, _ipinfo.Dataset);
            return _ipinfo.OverlapCalls == 2 ? new OverlapResult(OverlapOutcome.NoDataset, [], null) : answer;
        };

        AbuseNetworksResult result = await Find();

        AssertFailure(AbuseNetworksFailure.NoDataset, result);
        Assert.Equal([1000, 1], _calls.Select(call => call.Length));
    }

    // ---------- 5. A dataset change ----------

    [Fact]
    public async Task A_change_on_a_later_chunk_starts_again_once_and_then_succeeds()
    {
        SeedIpv4(1001);
        _ipinfo.OverlapFunc = blocks =>
        {
            if (_ipinfo.OverlapCalls == 2)
                _ipinfo.Dataset = Rebuilt;
            return Answer(blocks, _ipinfo.Dataset);
        };

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal([1000, 1, 1000, 1], _calls.Select(call => call.Length));
        Assert.Equal(Rebuilt, result.Dataset);
        Assert.Equal(1001, result.NotInIpinfo);
    }

    [Fact]
    public async Task A_change_before_the_first_chunk_is_caught_against_the_dataset_read_before_it()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        _ipinfo.OverlapFunc = blocks =>
        {
            if (_ipinfo.OverlapCalls == 1)
                _ipinfo.Dataset = Rebuilt;
            return Answer(blocks, _ipinfo.Dataset);
        };

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal(2, _ipinfo.OverlapCalls);
        Assert.Equal(Rebuilt, result.Dataset);
    }

    [Fact]
    public async Task A_swap_after_the_last_chunk_is_caught_by_the_end_check()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        _ipinfo.OverlapFunc = blocks =>
        {
            // The answer carries the dataset that answered, and the swap lands after it
            OverlapResult answer = Answer(blocks, _ipinfo.Dataset);
            if (_ipinfo.OverlapCalls == 1)
                _ipinfo.Dataset = Rebuilt;
            return answer;
        };

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal(2, _ipinfo.OverlapCalls);
        Assert.Equal(Rebuilt, result.Dataset);
    }

    [Fact]
    public async Task A_change_on_both_attempts_gives_DatasetChanged()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        _groups = _ => [Group("AS15169", "Google LLC", "google.com", "US")];
        _ipinfo.OverlapFunc = blocks =>
        {
            DatasetInfo current = _ipinfo.Dataset!;
            _ipinfo.Dataset = current with { RangeCount = current.RangeCount + 1 };
            return Answer(blocks, _ipinfo.Dataset);
        };

        AbuseNetworksResult result = await Find();

        AssertFailure(AbuseNetworksFailure.DatasetChanged, result);
        Assert.Equal(2, _ipinfo.OverlapCalls);
        Assert.Equal(Downloaded, result.LastDownloadAt);
    }

    [Fact]
    public async Task A_dataset_read_again_with_the_same_values_is_no_change()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        _ipinfo.OverlapFunc = blocks => Answer(blocks, _ipinfo.Dataset! with { });

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal(1, _ipinfo.OverlapCalls);
    }

    [Fact]
    public async Task A_change_on_chunk_2_of_3_starts_again_before_chunk_3()
    {
        SeedIpv4(2001);
        _ipinfo.OverlapFunc = blocks =>
        {
            if (_ipinfo.OverlapCalls == 2)
                _ipinfo.Dataset = Rebuilt;
            return Answer(blocks, _ipinfo.Dataset);
        };

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal([1000, 1000, 1000, 1000, 1], _calls.Select(call => call.Length));
        Assert.Equal(Rebuilt, result.Dataset);
    }

    [Fact]
    public async Task Chunk_1_is_compared_with_the_dataset_read_before_it_not_only_with_later_chunks()
    {
        SeedIpv4(1001);
        _ipinfo.OverlapFunc = blocks =>
        {
            if (_ipinfo.OverlapCalls == 1)
                _ipinfo.Dataset = Rebuilt;
            return Answer(blocks, _ipinfo.Dataset);
        };

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal([1000, 1000, 1], _calls.Select(call => call.Length));
        Assert.Equal(Rebuilt, result.Dataset);
    }

    [Fact]
    public async Task A_failure_other_than_a_change_on_the_second_attempt_is_returned_as_it_is()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        _ipinfo.OverlapFunc = blocks =>
        {
            if (_ipinfo.OverlapCalls == 1)
            {
                _ipinfo.Dataset = Rebuilt;
                return Answer(blocks, _ipinfo.Dataset);
            }

            return new OverlapResult(OverlapOutcome.Failed, [], null);
        };

        AbuseNetworksResult result = await Find();

        AssertFailure(AbuseNetworksFailure.LookupFailed, result);
        Assert.Equal(2, _ipinfo.OverlapCalls);
    }

    // ---------- 6. IPv6 off ----------

    [Fact]
    public async Task With_IPv6_off_no_IPv6_block_is_sent()
    {
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"), Entry("9.9.9.9"));

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Single(_calls);
        Assert.DoesNotContain(_calls[0], block => block.IsIpv6);
        Assert.False(result.IncludesIPv6);
        Assert.Equal(2, result.IPv4Addresses);
        Assert.Equal(0, result.IPv6Addresses);
    }

    [Fact]
    public async Task IPv6_turned_off_during_a_run_starts_again_once_and_gives_the_IPv4_result()
    {
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = true };
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        _ipinfo.OverlapFunc = blocks =>
        {
            if (_ipinfo.OverlapCalls == 1)
                _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
            return Answer(blocks, _ipinfo.Dataset);
        };

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal([false, true, false], _calls.Select(call => call[0].IsIpv6));
        Assert.False(result.IncludesIPv6);
        Assert.Equal(1, result.IPv4Addresses);
        Assert.Equal(0, result.IPv6Addresses);
        Assert.Equal(1, result.NotInIpinfo);
    }

    [Fact]
    public async Task IPv6_turned_on_during_a_run_starts_again_once_and_includes_IPv6()
    {
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        _ipinfo.OverlapFunc = blocks =>
        {
            if (_ipinfo.OverlapCalls == 1)
                _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = true };
            return Answer(blocks, _ipinfo.Dataset);
        };

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal([false, false, true], _calls.Select(call => call[0].IsIpv6));
        Assert.True(result.IncludesIPv6);
        Assert.Equal(1, result.IPv4Addresses);
        Assert.Equal(1, result.IPv6Addresses);
    }

    [Fact]
    public async Task IPv6_turned_off_after_the_last_IPv6_chunk_answered_starts_again_with_no_IPv6()
    {
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = true };
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        _ipinfo.OverlapFunc = blocks =>
        {
            // The IPv6 chunk is answered whole, and the switch turns off after it
            OverlapResult answer = Answer(blocks, _ipinfo.Dataset);
            if (_ipinfo.OverlapCalls == 2)
                _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
            return answer;
        };

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal([false, true, false], _calls.Select(call => call[0].IsIpv6));
        Assert.False(result.IncludesIPv6);
        Assert.Equal(1, result.IPv4Addresses);
        Assert.Equal(0, result.IPv6Addresses);
    }

    // ---------- 7. List states ----------

    [Fact]
    public async Task A_missing_list_has_no_download()
    {
        AbuseNetworksResult result = await Find();

        AssertFailure(AbuseNetworksFailure.NoDownload, result);
        Assert.Equal(0, _ipinfo.OverlapCalls);
        Assert.False(File.Exists(ListPath));
    }

    [Theory]
    [InlineData(0, AbuseNetworksFailure.None)]
    [InlineData(1, AbuseNetworksFailure.ClockWrong)]
    public async Task A_clock_exactly_24_hours_behind_the_last_download_groups_and_a_second_more_is_ClockWrong(
        int extraSeconds, AbuseNetworksFailure expected)
    {
        DateTimeOffset downloaded = Now.AddHours(AbuseIpDbLimits.MaxClockDifferenceHours).AddSeconds(extraSeconds);
        Seed(downloaded.AddSeconds(-2), downloaded, Entry("8.8.8.8"));

        AbuseNetworksResult result = await Find();

        if (expected == AbuseNetworksFailure.None)
        {
            Assert.Equal(AbuseNetworksFailure.None, result.Failure);
            Assert.Equal(1, result.IPv4Addresses);
            Assert.True(_ipinfo.OverlapCalls > 0);
        }
        else
        {
            AssertFailure(expected, result);
            Assert.Equal(0, _ipinfo.OverlapCalls);
        }
    }

    [Fact]
    public async Task A_list_with_only_failed_attempts_has_no_download()
    {
        Assert.Equal(AbuseListState.Usable, new AbuseIpDbList(ListPath).RecordAttempt(Now.AddMinutes(-5)));

        AbuseNetworksResult result = await Find();

        AssertFailure(AbuseNetworksFailure.NoDownload, result);
        Assert.Equal(0, _ipinfo.OverlapCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_newer_or_unreadable_list_is_reported_and_left_as_it_is(bool newer)
    {
        if (newer)
            WriteNewerList();
        else
            WriteUnreadableList();
        byte[] before = File.ReadAllBytes(ListPath);

        AbuseNetworksResult result = await Find();

        AssertFailure(newer ? AbuseNetworksFailure.ListNewer : AbuseNetworksFailure.ListUnreadable, result);
        Assert.Equal(0, _ipinfo.OverlapCalls);
        Assert.Null(result.LastDownloadAt);
        Assert.Equal(before, File.ReadAllBytes(ListPath));
    }

    [Fact]
    public async Task A_download_with_nothing_within_the_keep_days_gives_an_empty_result_and_no_lookup()
    {
        // Within the horizon, so the row stays in the file, but outside 10 keep days
        Seed(Generated.AddDays(-15), Downloaded.AddDays(-15), Entry("8.8.8.8"));
        Seed(Generated, Downloaded);
        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 10 };
        var reports = new List<GroupingProgress>();

        AbuseNetworksResult result = await Service().FindOffendingNetworksAsync(
            new ImmediateProgress<GroupingProgress>(reports.Add), CancellationToken.None);

        ListRead<ListFeed> held = new AbuseIpDbList(ListPath).ReadFeed(Now, TimeSpan.FromDays(365), true);
        Assert.Equal(AbuseListState.Usable, held.State);
        Assert.Equal(["8.8.8.8"], held.Value!.Entries.Select(entry => entry.Address.Text));
        Assert.Equal(new AbuseNetworksResult(AbuseNetworksFailure.None, result.Networks, 0, result.NoAsnCountries, 0, 0, 0,
                                             true, 10, Downloaded, Original, 0), result);
        Assert.Empty(result.Networks);
        Assert.Empty(result.NoAsnCountries);
        Assert.Equal(0, _ipinfo.OverlapCalls);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task The_keep_days_count_back_from_the_last_download_as_the_feed_does()
    {
        Seed(Generated.AddDays(-15), Downloaded.AddDays(-15), Entry("9.9.9.9"));
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        // A clock 20 days ahead of the last download hides nothing
        _time.SetUtcNow(Downloaded.AddDays(20));

        AbuseNetworksResult kept = await Find();
        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 10 };
        AbuseNetworksResult shorter = await Find();

        Assert.Equal(2, kept.IPv4Addresses);
        Assert.Equal(30, kept.KeepDays);
        Assert.Equal(1, shorter.IPv4Addresses);
        Assert.Equal(10, shorter.KeepDays);
        Assert.Equal(["8.8.8.8/32", "9.9.9.9/32", "8.8.8.8/32"], _calls.SelectMany(call => call).Select(block => block.Text));
    }

    [Fact]
    public async Task A_list_that_cant_be_opened_is_a_storage_error_with_no_lookup()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("File modes are a Unix check here.");
            return;
        }

        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        UnixFileMode mode = File.GetUnixFileMode(ListPath);
        File.SetUnixFileMode(ListPath, UnixFileMode.None);
        AbuseNetworksResult result;
        try
        {
            result = await Find();
        }
        finally
        {
            File.SetUnixFileMode(ListPath, mode);
        }

        AssertFailure(AbuseNetworksFailure.StorageError, result);
        Assert.Equal(0, _ipinfo.OverlapCalls);
    }

    [Fact]
    public async Task The_feed_cap_leaves_out_the_least_recently_listed_and_it_is_never_looked_up()
    {
        // Listed a day earlier, so still within the keep days and the horizon, but the least recent. Its bytes are
        // below every later address, so only the recency ranking, not a byte order, leaves it out
        Seed(Generated.AddDays(-1), Downloaded.AddDays(-1), Entry("8.7.255.255"));
        // 8.8.0.0 up to 8.9.255.255, all public
        BlacklistEntry[] later = Enumerable.Range(0, AbuseIpDbLimits.MaxFeedEntries)
            .Select(i => Entry($"8.{8 + (i >> 16)}.{(i >> 8) & 0xFF}.{i & 0xFF}")).ToArray();
        Seed(Generated, Downloaded, later);

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal(1, result.LeftOut);
        Assert.Equal(AbuseIpDbLimits.MaxFeedEntries, result.IPv4Addresses);
        Assert.Equal(0, result.IPv6Addresses);
        string[] asked = _calls.SelectMany(call => call).Select(block => block.Text).ToArray();
        Assert.Equal(AbuseIpDbLimits.MaxFeedEntries, asked.Length);
        Assert.DoesNotContain("8.7.255.255/32", asked);
    }

    [Fact]
    public async Task The_IPv6_family_is_capped_too_and_LeftOut_counts_both_families()
    {
        // The least recent IPv6 address, listed a day earlier, with the lowest bytes of its family
        Seed(Generated.AddDays(-1), Downloaded.AddDays(-1), Entry("2001:4860::1"));
        // 2001:4860:0:1:: up to 2001:4860:0:2::ffff, inside 2001:4860::/32, so outside the 2001::/23 deny block
        BlacklistEntry[] later = Enumerable.Range(0, AbuseIpDbLimits.MaxFeedEntries)
            .Select(i => Entry($"2001:4860:0:{1 + (i >> 16):x}::{i & 0xFFFF:x}"))
            .Append(Entry("8.8.8.8")).ToArray();
        Seed(Generated, Downloaded, later);

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal(1, result.LeftOut);
        Assert.Equal(1, result.IPv4Addresses);
        Assert.Equal(AbuseIpDbLimits.MaxFeedEntries, result.IPv6Addresses);
        string[] asked = _calls.SelectMany(call => call).Select(block => block.Text).ToArray();
        Assert.Equal(AbuseIpDbLimits.MaxFeedEntries + 1, asked.Length);
        Assert.Contains("8.8.8.8/32", asked);
        Assert.DoesNotContain("2001:4860::1/128", asked);
    }

    // ---------- 8. Cancellation ----------

    [Fact]
    public async Task A_cancel_before_the_call_throws()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().FindOffendingNetworksAsync(null, cts.Token));
        Assert.Equal(0, _ipinfo.OverlapCalls);
    }

    [Fact]
    public async Task A_cancel_between_chunks_throws_before_the_next_chunk()
    {
        SeedIpv4(1001);
        using var cts = new CancellationTokenSource();
        _ipinfo.OverlapFunc = blocks =>
        {
            cts.Cancel();
            return Answer(blocks, _ipinfo.Dataset);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().FindOffendingNetworksAsync(null, cts.Token));
        Assert.Equal(1, _ipinfo.OverlapCalls);
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_callers_fails_the_lookup()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        _ipinfo.OverlapFunc = _ => throw new OperationCanceledException();

        AbuseNetworksResult result = await Find();

        AssertFailure(AbuseNetworksFailure.LookupFailed, result);
    }

    [Fact]
    public async Task Something_unexpected_fails_the_lookup()
    {
        _ipinfo.SettingsFailure = new InvalidOperationException("settings");

        AbuseNetworksResult result = await Find();

        AssertFailure(AbuseNetworksFailure.LookupFailed, result);
    }

    // ---------- 9. Progress ----------

    [Fact]
    public async Task Progress_counts_addresses_from_none_to_all()
    {
        SeedIpv4(2001);
        var reports = new List<GroupingProgress>();

        AbuseNetworksResult result = await Service().FindOffendingNetworksAsync(
            new ImmediateProgress<GroupingProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal([new(0, 2001), new(1000, 2001), new(2000, 2001), new(2001, 2001)], reports);
    }

    [Fact]
    public async Task Progress_never_goes_backwards_when_a_restart_changes_the_total()
    {
        SeedIpv4(1001);
        _ipinfo.OverlapFunc = blocks =>
        {
            if (_ipinfo.OverlapCalls == 2)
            {
                // A download lands and the dataset is swapped during the first attempt
                Seed(Generated, Downloaded, Entry("9.9.9.9"));
                _ipinfo.Dataset = Rebuilt;
            }
            return Answer(blocks, _ipinfo.Dataset);
        };
        var reports = new List<GroupingProgress>();

        AbuseNetworksResult result = await Service().FindOffendingNetworksAsync(
            new ImmediateProgress<GroupingProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal(1002, result.IPv4Addresses);
        Assert.Equal([new(0, 1001), new(1000, 1001), new(1002, 1002)], reports);
        for (int i = 1; i < reports.Count; i++)
            Assert.True((long)reports[i].Done * reports[i - 1].Total > (long)reports[i - 1].Done * reports[i].Total);
    }

    // ---------- 10. Exceptions, requests and the key ----------

    [Fact]
    public async Task Unreadable_exceptions_dont_matter_and_nothing_is_sent_or_read_from_the_key()
    {
        _ipinfo.Exceptions = ExceptionList.Unreadable;
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        _groups = _ => [Group("AS15169", "Google LLC", "google.com", "US")];

        AbuseNetworksResult result = await Find();

        Assert.Equal(AbuseNetworksFailure.None, result.Failure);
        Assert.Equal([15169u], result.Networks.Select(network => network.Asn));
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(0, _store.Reads);
    }

    [Fact]
    public async Task Readable_exceptions_take_no_address_out_of_the_report()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("8.8.4.4"), Entry("2001:4860::8888"));
        _groups = _ => [Group("AS15169", "Google LLC", "google.com", "US")];
        AbuseNetworksResult without = await Find();
        string[] askedWithout = _calls.SelectMany(call => call).Select(block => block.Text).Order(StringComparer.Ordinal).ToArray();
        _calls.Clear();

        // This is a report, like the ASN report: exceptions cut feeds only
        _ipinfo.Exceptions = ExceptionList.Create([Excepted("8.8.8.8/32"), Excepted("2001:4860::/32")]);
        AbuseNetworksResult with = await Find();

        Assert.Equal(AbuseNetworksFailure.None, with.Failure);
        Assert.Equal(2, with.IPv4Addresses);
        Assert.Equal(1, with.IPv6Addresses);
        Assert.Equal(0, with.LeftOut);
        Assert.Equal(askedWithout, _calls.SelectMany(call => call).Select(block => block.Text).Order(StringComparer.Ordinal));
        Assert.Contains("8.8.8.8/32", askedWithout);
        AbuseNetworkGroup google = Assert.Single(with.Networks);
        Assert.Equal(3, google.Addresses);
        Assert.Equal(without.Networks.Select(network => (network.Asn, network.Addresses)),
                     with.Networks.Select(network => (network.Asn, network.Addresses)));
    }

    // ---------- Helpers ----------

    private static ExceptionEntry Excepted(string network)
    {
        Assert.True(ExceptionEntry.TryParseNetwork(network, out CidrBlock? block, out _));
        return new ExceptionEntry(block, null);
    }

    private AbuseIpDbService Service() => new(_http, _ipinfo, _temp.Paths, new CoreOptions(), _time, _store, new FakeAbuseAccounting());

    private Task<AbuseNetworksResult> Find() => Service().FindOffendingNetworksAsync(null, CancellationToken.None);

    /// <summary>Records the call's blocks and answers each one the switch lets through with <see cref="_groups"/>.</summary>
    private OverlapResult Answer(IReadOnlyList<CidrBlock> blocks, DatasetInfo? dataset)
    {
        _calls.Add([.. blocks]);
        bool ipv6 = _ipinfo.Settings.IPv6Enabled;
        return new OverlapResult(OverlapOutcome.Found,
                                 blocks.Where(block => ipv6 || !block.IsIpv6).Select(block => new NetworkOverlap(block, _groups(block))).ToList(),
                                 dataset);
    }

    private static OverlapGroup Group(string? asn, string? name, string? domain, string? country) =>
        new(country, null, asn, name, domain, 1);

    private static void AssertFailure(AbuseNetworksFailure failure, AbuseNetworksResult result)
    {
        Assert.Equal(failure, result.Failure);
        Assert.Empty(result.Networks);
        Assert.Empty(result.NoAsnCountries);
        Assert.Equal(0, result.NoAsn);
        Assert.Equal(0, result.NotInIpinfo);
        Assert.Equal(0, result.IPv4Addresses);
        Assert.Equal(0, result.IPv6Addresses);
        Assert.Equal(0, result.LeftOut);
        Assert.Null(result.Dataset);
    }

    /// <summary>Merges a blacklist generated and downloaded at the given times into the service's list file.</summary>
    private void Seed(DateTimeOffset generatedAt, DateTimeOffset downloadedAt, params BlacklistEntry[] entries)
    {
        ListRead<ListMerge> merged = new AbuseIpDbList(ListPath)
            .Merge(new Blacklist(generatedAt, entries, 0, 0, 0), downloadedAt, downloadedAt.AddDays(-30), CancellationToken.None);
        Assert.Equal(AbuseListState.Usable, merged.State);
    }

    /// <summary>Seeds that many IPv4 addresses from 8.8.0.0 up.</summary>
    private void SeedIpv4(int count) =>
        Seed(Generated, Downloaded, Enumerable.Range(0, count).Select(i => Entry($"8.8.{i / 256}.{i % 256}")).ToArray());

    private static byte[] Bytes(string text)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
        return address.GetAddressBytes();
    }

    private static PublicAddress Address(string text)
    {
        Assert.True(PublicAddress.TryCreate(Bytes(text), out PublicAddress? value));
        return value;
    }

    private static BlacklistEntry Entry(string address, string? country = null) => new(Address(address), 100, null, country);

    /// <summary>A list file a newer version wrote: its metadata says schema version 2.</summary>
    private void WriteNewerList()
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = ListPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE addresses (ip BLOB PRIMARY KEY, seen INTEGER NOT NULL);
            INSERT INTO metadata (key, value) VALUES ('schema_version', '2');
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>A list file that isn't a database.</summary>
    private void WriteUnreadableList()
    {
        byte[] bytes = new byte[4096];
        new Random(4711).NextBytes(bytes);
        File.WriteAllBytes(ListPath, bytes);
    }
}
