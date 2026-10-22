using System.Text;
using PoorMansFeed.Core.Config;

namespace PoorMansFeed.Tests;

public sealed class ConfigSealTests
{
    private const string FileId = "0123456789abcdef0123456789abcdef";

    private static readonly byte[] Plain = Encoding.UTF8.GetBytes("{\"ipv6Enabled\":true}");

    private static ConfigSeal NewSeal(string fileId = FileId) => new(ConfigSeal.NewKey(), fileId);

    [Theory]
    [InlineData((int)ConfigSection.Settings)]
    [InlineData((int)ConfigSection.Exceptions)]
    [InlineData((int)ConfigSection.Accounting)]
    public void Each_section_round_trips(int sectionValue)
    {
        // An int, because a public test method can't take the internal enum
        var section = (ConfigSection)sectionValue;
        using var seal = NewSeal();

        byte[] blob = seal.Seal(section, 1, Plain);

        Assert.True(seal.TryOpen(section, 1, blob, out byte[]? opened));
        Assert.Equal(Plain, opened);
    }

    [Fact]
    public void An_empty_plaintext_seals_to_the_header_alone_and_opens_to_empty()
    {
        using var seal = NewSeal();

        byte[] blob = seal.Seal(ConfigSection.Settings, 1, []);

        Assert.Equal(29, blob.Length);
        Assert.True(seal.TryOpen(ConfigSection.Settings, 1, blob, out byte[]? opened));
        Assert.Empty(opened);
    }

    [Fact]
    public void A_megabyte_round_trips()
    {
        using var seal = NewSeal();
        byte[] large = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i * 7)).ToArray();

        byte[] blob = seal.Seal(ConfigSection.Exceptions, 3, large);

        Assert.True(seal.TryOpen(ConfigSection.Exceptions, 3, blob, out byte[]? opened));
        Assert.Equal(large, opened);
    }

    [Fact]
    public void The_blob_is_the_version_byte_then_29_header_bytes_and_the_ciphertext()
    {
        using var seal = NewSeal();

        byte[] blob = seal.Seal(ConfigSection.Settings, 1, Plain);

        Assert.Equal(1, blob[0]);
        Assert.Equal(ConfigSeal.HeaderBytes + Plain.Length, blob.Length);
        Assert.Equal(29, ConfigSeal.HeaderBytes);
    }

    [Theory]
    [InlineData(0)]                     // the version
    [InlineData(1)]                     // the nonce
    [InlineData(12)]
    [InlineData(13)]                    // the tag
    [InlineData(28)]
    [InlineData(29)]                    // the ciphertext
    [InlineData(-1)]
    public void One_changed_byte_fails_to_open(int index)
    {
        using var seal = NewSeal();
        byte[] blob = seal.Seal(ConfigSection.Settings, 1, Plain);

        blob[index < 0 ? blob.Length + index : index] ^= 0x01;

        Assert.False(seal.TryOpen(ConfigSection.Settings, 1, blob, out byte[]? opened));
        Assert.Null(opened);
    }

    [Fact]
    public void Another_key_fails_to_open()
    {
        using var seal = NewSeal();
        using var other = NewSeal();
        byte[] blob = seal.Seal(ConfigSection.Settings, 1, Plain);

        Assert.False(other.TryOpen(ConfigSection.Settings, 1, blob, out _));
    }

    [Fact]
    public void Another_file_section_or_schema_version_fails_to_open()
    {
        byte[] key = ConfigSeal.NewKey();
        byte[] copy = [.. key];
        using var seal = new ConfigSeal(key, FileId);
        using var otherFile = new ConfigSeal(copy, "fedcba9876543210fedcba9876543210");
        byte[] blob = seal.Seal(ConfigSection.Settings, 1, Plain);

        Assert.False(otherFile.TryOpen(ConfigSection.Settings, 1, blob, out _));
        Assert.False(seal.TryOpen(ConfigSection.Exceptions, 1, blob, out _));
        Assert.False(seal.TryOpen(ConfigSection.Accounting, 1, blob, out _));
        Assert.False(seal.TryOpen(ConfigSection.Settings, 2, blob, out _));
        Assert.True(seal.TryOpen(ConfigSection.Settings, 1, blob, out _));
    }

    [Fact]
    public void The_same_key_and_file_id_in_a_new_seal_opens_the_blob()
    {
        // The associated data is rebuilt from the arguments, so a seal made again at the next launch opens what was saved
        byte[] key = ConfigSeal.NewKey();
        byte[] copy = [.. key];
        using var first = new ConfigSeal(key, FileId);
        byte[] blob = first.Seal(ConfigSection.Accounting, 4, Plain);
        using var second = new ConfigSeal(copy, FileId);

        Assert.True(second.TryOpen(ConfigSection.Accounting, 4, blob, out byte[]? opened));
        Assert.Equal(Plain, opened);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(64)]
    public void Junk_blobs_give_false_and_never_throw(int length)
    {
        using var seal = NewSeal();
        byte[] junk = Enumerable.Range(0, length).Select(i => (byte)(i + 1)).ToArray();
        if (length > 0)
            junk[0] = ConfigSeal.FormatVersion;

        Assert.False(seal.TryOpen(ConfigSection.Settings, 1, junk, out byte[]? opened));
        Assert.Null(opened);
    }

    [Fact]
    public void Two_seals_of_the_same_plaintext_differ()
    {
        using var seal = NewSeal();

        byte[] a = seal.Seal(ConfigSection.Settings, 1, Plain);
        byte[] b = seal.Seal(ConfigSection.Settings, 1, Plain);

        Assert.NotEqual(a.AsSpan(1, ConfigSeal.NonceBytes).ToArray(), b.AsSpan(1, ConfigSeal.NonceBytes).ToArray());
        Assert.NotEqual(a.AsSpan(ConfigSeal.HeaderBytes).ToArray(), b.AsSpan(ConfigSeal.HeaderBytes).ToArray());
    }

    [Fact]
    public void A_thousand_seals_have_a_thousand_nonces()
    {
        using var seal = NewSeal();

        var nonces = Enumerable.Range(0, 1000)
            .Select(_ => Convert.ToHexString(seal.Seal(ConfigSection.Settings, 1, Plain), 1, ConfigSeal.NonceBytes))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1000, nonces.Count);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void A_key_of_another_length_throws_and_is_zeroed(int length)
    {
        byte[] key = Enumerable.Repeat((byte)0xA5, length).ToArray();

        Assert.Throws<ArgumentException>(() => new ConfigSeal(key, FileId));

        Assert.All(key, b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789ABCDEF0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef 123456789abcdef")]
    [InlineData("")]
    public void A_bad_file_id_throws_and_the_key_is_zeroed(string fileId)
    {
        byte[] key = ConfigSeal.NewKey();
        key[0] = 0x5A;

        Assert.Throws<ArgumentException>(() => new ConfigSeal(key, fileId));

        Assert.All(key, b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_good_construction_zeroes_the_callers_key()
    {
        byte[] key = ConfigSeal.NewKey();
        key[0] = 0x5A;

        using var seal = new ConfigSeal(key, FileId);

        Assert.All(key, b => Assert.Equal(0, b));
    }

    [Fact]
    public void NewKey_gives_32_random_bytes()
    {
        byte[] a = ConfigSeal.NewKey();
        byte[] b = ConfigSeal.NewKey();

        Assert.Equal(32, a.Length);
        Assert.Equal(32, b.Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NewFileId_gives_32_lower_case_hex_digits()
    {
        string a = ConfigSeal.NewFileId();
        string b = ConfigSeal.NewFileId();

        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.Matches("^[0-9a-f]{32}$", b);
        Assert.NotEqual(a, b);
        using var seal = new ConfigSeal(ConfigSeal.NewKey(), a);
    }

    [Fact]
    public void The_section_names_are_fixed()
    {
        // They are the row names and part of every blob's associated data, so a change loses every saved section
        Assert.Equal("settings", ConfigSeal.SectionName(ConfigSection.Settings));
        Assert.Equal("exceptions", ConfigSeal.SectionName(ConfigSection.Exceptions));
        Assert.Equal("accounting", ConfigSeal.SectionName(ConfigSection.Accounting));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfigSeal.SectionName((ConfigSection)99));
    }
}
