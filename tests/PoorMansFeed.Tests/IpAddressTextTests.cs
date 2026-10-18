using System.Net;
using System.Runtime.CompilerServices;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>Only the plain form of an address is read, never the legacy or decorated forms IPAddress.TryParse takes.</summary>
public sealed class IpAddressTextTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("8.8.8.8")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:DB8:0:0:0:0:0:1")]
    [InlineData("0a01:0200::")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::1.2.3.4")]
    public void PlainAddressesAreReadAsIPAddressReadsThem(string text)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
        Assert.Equal(IPAddress.Parse(text), address);
    }

    [Theory]
    [InlineData("010.0.0.1")]    // IPAddress.TryParse reads octal: 8.0.0.1
    [InlineData("0x7f.0.0.1")]   // hex: 127.0.0.1
    [InlineData("10.1")]         // shorthand: 10.0.0.1
    [InlineData("192.168.1")]    // shorthand: 192.168.0.1
    [InlineData("3232235777")]   // one number: 192.168.1.1
    [InlineData("008.008.008.008")]
    [InlineData("256.0.0.1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2.3")]
    [InlineData("1.2.3.4.")]
    [InlineData(" 1.2.3.4")]
    [InlineData("1.2.3.4\n")]
    [InlineData("1.2.3.4:80")]
    [InlineData("\u0661.2.3.4")] // an Arabic-Indic digit one
    [InlineData("[::1]")]
    [InlineData("[::1]:80")]
    [InlineData("fe80::1%5")]
    [InlineData("fe80::1%eth0")]
    [InlineData("::ffff:010.8.8.8")]
    [InlineData("::ffff:1.2.3")]
    [InlineData("1.2::3.4.5.6")]
    [InlineData("1::2::3")]
    [InlineData("12345::")]
    [InlineData("::1\n")]
    [InlineData("")]
    public void LegacyAndDecoratedFormsAreRejected(string text)
    {
        Assert.False(IpAddressText.TryParse(text, out IPAddress? address));
        Assert.Null(address);
    }

    [Fact]
    public void OnlyIpAddressTextCallsTheFrameworkParser()
    {
        string sources = Path.Combine(RepositoryRoot(), "src");
        string separator = Path.DirectorySeparatorChar.ToString();
        var offenders = Directory
            .EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(separator + "obj" + separator, StringComparison.Ordinal)
                && !file.Contains(separator + "bin" + separator, StringComparison.Ordinal)
                && Path.GetFileName(file) != "IpAddressText.cs")
            .Where(file => File.ReadAllText(file) is var source
                && (source.Contains("IPAddress.TryParse(", StringComparison.Ordinal)
                    || source.Contains("IPAddress.Parse(", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    private static string RepositoryRoot([CallerFilePath] string testFile = "")
    {
        for (DirectoryInfo? directory = new FileInfo(testFile).Directory; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PoorMansFeed.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("The repository root wasn't found.");
    }
}
