using System.Reflection;
using System.Text;
using PoorMansFeed.Core;
using Xunit.Sdk;

namespace PoorMansFeed.Tests;

public sealed class FeedFormatTests
{
    // Environment.NewLine is LF on macOS too, so only a compile-time constant shows the value is LF on every OS
    [Fact]
    public void LineEnd_is_the_constant_LF()
    {
        FieldInfo? field = typeof(FeedText).GetField(nameof(FeedText.LineEnd), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.True(field.IsLiteral, "FeedText.LineEnd isn't a constant.");
        Assert.Equal("\n", field.GetRawConstantValue());
    }

    [Theory]
    [InlineData("8.8.8.8/32", "8.8.8.8")]
    [InlineData("2001:4860::8888/128", "2001:4860::8888")]
    [InlineData("8.8.8.8/032", "8.8.8.8")]
    [InlineData("::ffff:8.8.8.8/128", "::ffff:8.8.8.8")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("::ffff:8.8.8.8/32", "::ffff:8.8.8.8/32")]
    [InlineData("2001:db8::/127", "2001:db8::/127")]
    [InlineData("8.8.8.0/31", "8.8.8.0/31")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("::/0", "::/0")]
    [InlineData("8.8.8.8", "8.8.8.8")]
    [InlineData("2001:4860::8888", "2001:4860::8888")]
    [InlineData("8.8.8.0/24", "8.8.8.0/24")]
    public void Entry_writes_a_single_host_bare_and_leaves_every_other_network_as_it_is(string network, string expected)
    {
        Assert.Equal(expected, FeedText.Entry(network));
    }

    [Fact]
    public void Entry_returns_text_without_a_slash_as_the_same_string()
    {
        string network = "2001:4860::8888";

        Assert.Same(network, FeedText.Entry(network));
    }

    [Fact]
    public void A_well_formed_LF_feed_passes()
    {
        FeedFormat.AssertWellFormed(Bytes("# PoorMansFeed\n# Entries: 4\n8.8.8.8\n8.8.8.0/24\n2001:4860::8888\n2001:db8::/32\n"));
    }

    [Theory]
    [InlineData("# PoorMansFeed\r\n8.8.8.8\r\n")]
    [InlineData("# PoorMansFeed\n8.8.8.8\r\n")]
    [InlineData("# PoorMansFeed\n\n8.8.8.8\n")]
    [InlineData("\n# PoorMansFeed\n8.8.8.8\n")]
    [InlineData("# PoorMansFeed\n8.8.8.8/32\n")]
    [InlineData("# PoorMansFeed\n2001:4860::8888/128\n")]
    [InlineData("# PoorMansFeed\n8.8.8.8")]
    [InlineData("# PoorMansFeed\n8.8.8.8 \n")]
    [InlineData("# PoorMansFeed\nnot an address\n")]
    [InlineData("")]
    public void A_malformed_feed_fails(string text)
    {
        Assert.ThrowsAny<XunitException>(() => FeedFormat.AssertWellFormed(Bytes(text)));
    }

    [Fact]
    public void A_feed_with_a_byte_order_mark_fails()
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Bytes("# PoorMansFeed\n8.8.8.8\n")];

        Assert.ThrowsAny<XunitException>(() => FeedFormat.AssertWellFormed(bytes));
    }

    private static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);
}

/// <summary>What ModSecurity and FortiGate both read: LF lines, no blank line, and each host bare.</summary>
internal static class FeedFormat
{
    public static void AssertWellFormed(byte[] bytes)
    {
        byte[] bom = Encoding.UTF8.GetPreamble();
        Assert.False(bytes.AsSpan().StartsWith(bom), "The feed starts with a UTF-8 byte order mark.");
        int cr = Array.IndexOf(bytes, (byte)'\r');
        Assert.True(cr < 0, $"The feed holds a CR at byte {cr}.");
        Assert.True(bytes.Length > 0, "The feed is empty.");
        Assert.True(bytes[^1] == (byte)'\n', "The feed doesn't end with LF.");

        string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        string[] lines = text[..^1].Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int number = i + 1;
            Assert.True(line.Length > 0, $"Line {number} is blank.");
            if (line.StartsWith('#'))
                continue;

            Assert.False(line.Any(char.IsWhiteSpace), $"Line {number} holds whitespace: '{line}'.");
            (byte[] Start, byte[] End)? network;
            try
            {
                network = Cidr.Parse(line);
            }
            catch (FormatException)
            {
                network = null;
            }

            Assert.True(network is not null, $"Line {number} isn't an address or a network: '{line}'.");
            bool host = network.Value.Start.AsSpan().SequenceEqual(network.Value.End);
            Assert.False(host && line.Contains('/'), $"Line {number} is a single host not written bare: '{line}'.");
        }
    }
}
