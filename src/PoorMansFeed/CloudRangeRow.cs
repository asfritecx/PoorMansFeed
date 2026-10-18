using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.GoogleCloud;

namespace PoorMansFeed;

/// <summary>One region in the Google Cloud Lookup picker.</summary>
public sealed record RegionOption(GoogleCloudScope Scope, string Text);

/// <summary>One Google Cloud range line, already formatted. IsIncluded starts from the confirmation.</summary>
public sealed class CloudRangeRow : ObservableObject
{
    private bool _isIncluded;

    private CloudRangeRow(CidrBlock block, string networkText, string regionText, string countryText, string asText,
                          string statusText, RangeConfirmation status, bool isLargeRange, string accessibleText, bool isIncluded)
    {
        Block = block;
        NetworkText = networkText;
        RegionText = regionText;
        CountryText = countryText;
        AsText = asText;
        StatusText = statusText;
        Status = status;
        IsLargeRange = isLargeRange;
        AccessibleText = accessibleText;
        _isIncluded = isIncluded;
    }

    public static CloudRangeRow From(CheckedRange range)
    {
        IReadOnlyList<OverlapGroup> groups = range.Overlaps;
        string country = CountryTextOf(groups);
        string asText = AsTextOf(groups);
        string statusText = StatusTextOf(range.Status);
        string accessible = string.Join(", ", range.Prefix.Block.Text, range.Prefix.Scope, country, asText, statusText)
            + (range.IsLarge ? AppText.LargeRangeSpoken : "");

        return new CloudRangeRow(
            range.Prefix.Block,
            range.Prefix.Block.Text,
            range.Prefix.Scope,
            country,
            asText,
            statusText,
            range.Status,
            range.IsLarge,
            accessible,
            range.StartsIncluded);
    }

    public CidrBlock Block { get; }

    public string NetworkText { get; }

    public string RegionText { get; }

    public string CountryText { get; }

    public string AsText { get; }

    public string StatusText { get; }

    public RangeConfirmation Status { get; }

    public bool IsConfirmed => Status == RangeConfirmation.Confirmed;

    public bool IsMixed => Status == RangeConfirmation.Mixed;

    public bool IsLargeRange { get; }

    public string AccessibleText { get; }

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }

    private static string CountryTextOf(IReadOnlyList<OverlapGroup> groups)
    {
        var codes = new List<string>();
        foreach (OverlapGroup group in groups)
        {
            string? code = Clean(group.CountryCode);
            if (code is not null && !codes.Contains(code))
                codes.Add(code);
        }

        return codes.Count == 0 ? AppText.Missing : string.Join(", ", codes);
    }

    private static string AsTextOf(IReadOnlyList<OverlapGroup> groups)
    {
        if (groups.Count == 0)
            return AppText.Missing;

        OverlapGroup first = groups[0];
        string? asn = Clean(first.Asn);
        if (asn is not null && !asn.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
            asn = "AS" + asn;

        string text = Join(asn, Clean(first.AsName));
        if (groups.Count == 1)
            return text;

        return text + " +" + (groups.Count - 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string StatusTextOf(RangeConfirmation status) => status switch
    {
        RangeConfirmation.Confirmed => AppText.StatusConfirmed,
        RangeConfirmation.Mixed => AppText.StatusMixed,
        _ => AppText.StatusNotInDatabase,
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Join(string? first, string? second) => (first, second) switch
    {
        (not null, not null) => first + AppText.Joiner + second,
        (not null, null) => first,
        (null, not null) => second,
        _ => AppText.Missing,
    };
}
