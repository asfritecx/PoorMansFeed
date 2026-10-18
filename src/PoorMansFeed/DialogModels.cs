using CommunityToolkit.Mvvm.ComponentModel;
using PoorMansFeed.Core;

namespace PoorMansFeed;

/// <summary>Why the token dialog is shown: the first run, or the user asked for a new token.</summary>
public enum TokenPromptReason { FirstRun, Reset }

/// <summary>What the large-range dialog shows. Built by the view model; the dialog returns the networks ticked.</summary>
public sealed record LargeRangePrompt(string Title, string Message, IReadOnlyList<LargeRangeChoice> Choices);

/// <summary>One large range in the dialog. Unticked by default, so a large range is left out unless the user ticks it.</summary>
public sealed class LargeRangeChoice : ObservableObject
{
    private bool _isIncluded;

    public LargeRangeChoice(string network, string sizeText)
    {
        Network = network;
        SizeText = sizeText;
        AccessibleText = $"{network}, {sizeText}";
    }

    public string Network { get; }

    public string SizeText { get; }

    public string AccessibleText { get; }

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }

    public static LargeRangeChoice From(LargeRange range)
    {
        string size = range.IsIpv6 && range.PrefixLength <= 64
            ? AppText.Subnets64(System.Numerics.BigInteger.Pow(2, 64 - range.PrefixLength))
            : AppText.Addresses(range.AddressCount);
        return new LargeRangeChoice(range.Network, size);
    }
}
