using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PoorMansFeed.Core.Config;
using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Tests;

public sealed class ConfigKeyStoreTests
{
    // 32 distinct known bytes, so a trace holding them in any common text form can be found
    private static byte[] SentinelKey() => Enumerable.Range(0, 32).Select(i => (byte)(0xC0 + i)).ToArray();

    public static TheoryData<Exception> UnavailableReads =>
    [
        new SecretStoreException("The Keychain couldn't read the secret."),
        new IOException(),
        new UnauthorizedAccessException(),
    ];

    public static TheoryData<Exception> WriteFailures =>
    [
        new SecretStoreException("The Keychain couldn't write the secret."),
        new CryptographicException(),
        new IOException(),
        new UnauthorizedAccessException(),
    ];

    private static InMemorySecretStore Holding(byte[] secret) => new() { Secret = [.. secret], Marker = true };

    [Fact]
    public void TryLoad_reads_a_32_byte_key()
    {
        byte[] key = SentinelKey();

        Assert.Equal(KeyState.Read, new ConfigKeyStore(Holding(key)).TryLoad(out byte[]? loaded));

        Assert.Equal(key, loaded);
    }

    [Fact]
    public void TryLoad_without_a_marker_is_Missing_and_reads_nothing()
    {
        var store = new InMemorySecretStore { Secret = SentinelKey() };
        var keys = new ConfigKeyStore(store);

        Assert.False(keys.Exists);
        Assert.Equal(KeyState.Missing, keys.TryLoad(out byte[]? loaded));

        Assert.Null(loaded);
        Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void TryLoad_of_another_length_is_Unusable_and_kept(int length)
    {
        var store = Holding(new byte[length]);

        Assert.Equal(KeyState.Unusable, new ConfigKeyStore(store).TryLoad(out byte[]? loaded));

        Assert.Null(loaded);
        Assert.True(store.Exists);
        Assert.NotNull(store.Secret);
    }

    [Fact]
    public void TryLoad_of_a_marker_without_its_key_is_Unusable()
    {
        var store = new InMemorySecretStore { Marker = true };

        Assert.Equal(KeyState.Unusable, new ConfigKeyStore(store).TryLoad(out byte[]? loaded));

        Assert.Null(loaded);
        Assert.True(store.Exists);
    }

    [Fact]
    public void TryLoad_of_a_key_that_can_never_be_read_is_Unusable_and_never_deletes_it()
    {
        var store = Holding(SentinelKey());
        store.ReadFailure = new CryptographicException("The data is protected for another account.");

        Assert.Equal(KeyState.Unusable, new ConfigKeyStore(store).TryLoad(out byte[]? loaded));

        Assert.Null(loaded);
        Assert.True(store.Exists);
        Assert.Equal(SentinelKey(), store.Secret);
    }

    [Theory]
    [MemberData(nameof(UnavailableReads))]
    public void TryLoad_of_a_store_that_cant_be_reached_now_is_Unavailable_and_keeps_the_key(Exception failure)
    {
        var store = Holding(SentinelKey());
        store.ReadFailure = failure;

        Assert.Equal(KeyState.Unavailable, new ConfigKeyStore(store).TryLoad(out byte[]? loaded));

        Assert.Null(loaded);
        Assert.True(store.Exists);
        Assert.Equal(SentinelKey(), store.Secret);
    }

    [Fact]
    public void A_system_without_a_store_is_NoStore()
    {
        var keys = new ConfigKeyStore(new SecretStores.UnavailableSecretStore());

        Assert.Equal(KeyState.NoStore, keys.TryLoad(out byte[]? loaded));
        Assert.Null(loaded);
        Assert.Equal(KeyState.NoStore, keys.Replace(SentinelKey()));
    }

    [Fact]
    public void Replace_saves_the_key_and_leaves_the_callers_array_as_it_was()
    {
        var store = new InMemorySecretStore();
        byte[] key = SentinelKey();

        Assert.Equal(KeyState.Read, new ConfigKeyStore(store).Replace(key));

        Assert.Equal(SentinelKey(), store.Secret);
        Assert.Equal(SentinelKey(), key);
        Assert.True(store.Exists);
    }

    [Fact]
    public void Replace_is_Unavailable_when_the_write_saves_nothing()
    {
        var store = new InMemorySecretStore { DropWrites = true };
        byte[] key = SentinelKey();

        Assert.Equal(KeyState.Unavailable, new ConfigKeyStore(store).Replace(key));

        Assert.Equal(SentinelKey(), key);
    }

    [Fact]
    public void Replace_is_Unavailable_when_other_bytes_come_back()
    {
        var store = new InMemorySecretStore { ReadsOtherBytes = true };
        byte[] key = SentinelKey();

        Assert.Equal(KeyState.Unavailable, new ConfigKeyStore(store).Replace(key));

        Assert.Equal(SentinelKey(), key);
    }

    [Theory]
    [MemberData(nameof(WriteFailures))]
    public void Replace_is_Unavailable_when_the_store_fails(Exception failure)
    {
        var store = new InMemorySecretStore { WriteFailure = failure };
        byte[] key = SentinelKey();

        Assert.Equal(KeyState.Unavailable, new ConfigKeyStore(store).Replace(key));

        Assert.Equal(SentinelKey(), key);
    }

    [Fact]
    public void Replace_refuses_a_key_of_another_length()
    {
        var store = new InMemorySecretStore();

        Assert.Throws<ArgumentException>(() => new ConfigKeyStore(store).Replace(new byte[31]));

        Assert.False(store.Exists);
    }

    [Fact]
    public void Raw_bytes_round_trip()
    {
        // The key is raw bytes, not text, so zero and 0xFF bytes come back as they were saved
        byte[] key = [0x00, 0xFF, .. Enumerable.Range(1, 28).Select(i => (byte)i), 0xFF, 0x00];
        var keys = new ConfigKeyStore(new InMemorySecretStore());

        Assert.Equal(KeyState.Read, keys.Replace(key));
        Assert.Equal(KeyState.Read, keys.TryLoad(out byte[]? loaded));

        Assert.Equal(key, loaded);
    }

    [Fact]
    public void Failures_never_trace_the_key()
    {
        byte[] key = SentinelKey();
        string[] forms = [Convert.ToBase64String(key), Convert.ToHexString(key), Convert.ToHexStringLower(key)];
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            // Reads: wrong lengths (holding the key's own bytes), a marker alone, and every read failure
            new ConfigKeyStore(Holding([.. key, 0x01])).TryLoad(out _);
            new ConfigKeyStore(Holding(key[..31])).TryLoad(out _);
            new ConfigKeyStore(new InMemorySecretStore { Marker = true }).TryLoad(out _);
            foreach (Exception failure in new Exception[]
                     {
                         new CryptographicException(), new SecretStoreException("The Keychain couldn't read the secret."),
                         new IOException(), new UnauthorizedAccessException(),
                     })
            {
                var store = Holding(key);
                store.ReadFailure = failure;
                new ConfigKeyStore(store).TryLoad(out _);
            }

            // Replaces: a dropped write, other bytes back, and every write failure
            new ConfigKeyStore(new InMemorySecretStore { DropWrites = true }).Replace(key);
            new ConfigKeyStore(new InMemorySecretStore { ReadsOtherBytes = true }).Replace(key);
            foreach (Exception failure in new Exception[]
                     {
                         new SecretStoreException("The Keychain couldn't write the secret."), new CryptographicException(),
                         new IOException(), new UnauthorizedAccessException(),
                     })
            {
                new ConfigKeyStore(new InMemorySecretStore { WriteFailure = failure }).Replace(key);
            }

            // A seal under the key that fails to open
            using (var seal = new ConfigSeal([.. key], ConfigSeal.NewFileId()))
            {
                byte[] blob = seal.Seal(ConfigSection.Settings, 1, Encoding.UTF8.GetBytes("{}"));
                blob[^1] ^= 0x01;
                Assert.False(seal.TryOpen(ConfigSection.Settings, 1, blob, out _));
            }
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        Assert.Contains("config key", listener.Text, StringComparison.Ordinal);
        foreach (string form in forms)
            Assert.DoesNotContain(form, listener.Text, StringComparison.Ordinal);
    }

    // Trace listeners are process-wide, so this also hears other tests; it only looks for a text they don't write
    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                    return _text.ToString();
            }
        }

        public override void Write(string? message)
        {
            lock (_text)
                _text.Append(message);
        }

        public override void WriteLine(string? message)
        {
            lock (_text)
                _text.AppendLine(message);
        }
    }
}
