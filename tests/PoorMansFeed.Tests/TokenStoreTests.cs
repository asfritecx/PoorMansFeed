using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class TokenStoreTests
{
    [Fact]
    public void Save_then_TryLoad_returns_same_text()
    {
        using var temp = new TempFolder();
        var store = new TokenStore(temp.File(".ipinfo_token"));
        const string token = "abc123-t\u00F6ken-SECRET";

        Assert.True(store.Save(token));

        Assert.Equal(token, store.TryLoad());
    }

    [Fact]
    public void Saved_file_decrypts_from_its_own_salt_without_machine_or_user_name()
    {
        using var temp = new TempFolder();
        string path = temp.File(".ipinfo_token");
        var store = new TokenStore(path);
        Assert.True(store.Save("abc123"));

        // The host name changes with the network on macOS, so the key must not depend on it.
        byte[] file = File.ReadAllBytes(path);
        byte[] key = CryptoHelper.DeriveKey("IPInfoLiteParser", file[..16]);

        Assert.Equal("abc123", CryptoHelper.Decrypt(file[16..], key));
    }

    [Fact]
    public void Save_uses_a_new_salt_each_time()
    {
        using var temp = new TempFolder();
        string path = temp.File(".ipinfo_token");
        var store = new TokenStore(path);

        Assert.True(store.Save("abc123"));
        byte[] first = File.ReadAllBytes(path)[..16];
        Assert.True(store.Save("abc123"));
        byte[] second = File.ReadAllBytes(path)[..16];

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void HasToken_is_false_when_missing_and_true_after_Save()
    {
        using var temp = new TempFolder();
        var store = new TokenStore(temp.File(".ipinfo_token"));

        Assert.False(store.HasToken);

        Assert.True(store.Save("abc123"));

        Assert.True(store.HasToken);
    }

    [Fact]
    public void Clear_removes_the_token_file()
    {
        using var temp = new TempFolder();
        string path = temp.File(".ipinfo_token");
        var store = new TokenStore(path);
        Assert.True(store.Save("abc123"));

        Assert.True(store.Clear());

        Assert.False(File.Exists(path));
        Assert.False(store.HasToken);
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public void TryLoad_on_too_short_file_returns_null_and_deletes_it()
    {
        using var temp = new TempFolder();
        string path = temp.File(".ipinfo_token");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        var store = new TokenStore(path);

        Assert.Null(store.TryLoad());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryLoad_on_corrupt_file_returns_null_and_deletes_it()
    {
        using var temp = new TempFolder();
        string path = temp.File(".ipinfo_token");
        // 16-byte salt, 16-byte IV plus 17 bytes: not a whole number of AES blocks, so decryption always fails.
        File.WriteAllBytes(path, new byte[49]);
        var store = new TokenStore(path);

        Assert.Null(store.TryLoad());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Save_sets_owner_only_permissions_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var temp = new TempFolder();
        string path = temp.File(".ipinfo_token");
        var store = new TokenStore(path);

        Assert.True(store.Save("abc123"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Save_replaces_a_readable_token_file_with_an_owner_only_one_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var temp = new TempFolder();
        string path = temp.File(".ipinfo_token");
        File.WriteAllBytes(path, new byte[49]);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var store = new TokenStore(path);

        Assert.True(store.Save("abc123"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal("abc123", store.TryLoad());
    }

    [Fact]
    public void Save_does_not_reuse_a_readable_leftover_temporary_file_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var temp = new TempFolder();
        string path = temp.File(".ipinfo_token");
        File.WriteAllBytes(path + ".tmp", [1, 2, 3]);
        File.SetUnixFileMode(path + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var store = new TokenStore(path);

        Assert.True(store.Save("abc123"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal("abc123", store.TryLoad());
    }

    [Fact]
    public void Save_leaves_no_temporary_file()
    {
        using var temp = new TempFolder();
        string path = temp.File(".ipinfo_token");
        var store = new TokenStore(path);

        Assert.True(store.Save("abc123"));
        Assert.True(store.Save("def456"));

        Assert.Equal([path], Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void Failed_Save_keeps_the_saved_token()
    {
        using var temp = new TempFolder();
        string path = temp.File(".ipinfo_token");
        var store = new TokenStore(path);
        Assert.True(store.Save("abc123"));
        // A folder in the temporary file's place makes the write fail.
        Directory.CreateDirectory(path + ".tmp");

        Assert.False(store.Save("def456"));

        Assert.Equal("abc123", store.TryLoad());
    }
}
