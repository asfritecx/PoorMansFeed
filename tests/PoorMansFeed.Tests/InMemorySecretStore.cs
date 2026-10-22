using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Tests;

/// <summary>
/// A secret store in memory, for every test that saves a token. The real stores are one per user account (the Keychain
/// item has a fixed name), so a test that reached one would replace the developer's own token.
/// </summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    /// <summary>The saved bytes, as the real store would hold them.</summary>
    public byte[]? Secret { get; set; }

    /// <summary>What Exists answers, as the Keychain marker would: set by a write, cleared by a delete.</summary>
    public bool Marker { get; set; }

    public int Reads { get; private set; }

    /// <summary>A write that reports success but saves nothing.</summary>
    public bool DropWrites { get; set; }

    /// <summary>A read that gives back other bytes than were saved.</summary>
    public bool ReadsOtherBytes { get; set; }

    public Exception? ReadFailure { get; set; }

    public Exception? WriteFailure { get; set; }

    /// <summary>A delete that fails keeps the secret and the marker, as both real stores do.</summary>
    public Exception? DeleteFailure { get; set; }

    /// <summary>Runs at the start of every read, so a test can hold a caller inside one.</summary>
    public Action? BeforeRead { get; set; }

    public bool Exists => Marker;

    public byte[]? Read()
    {
        BeforeRead?.Invoke();
        Reads++;
        if (ReadFailure is not null)
            throw ReadFailure;
        if (Secret is null)
            return null;
        return ReadsOtherBytes ? [.. Secret, 0x21] : [.. Secret];
    }

    public void Write(byte[] secret)
    {
        if (WriteFailure is not null)
            throw WriteFailure;
        if (!DropWrites)
            Secret = [.. secret];
        Marker = true;
    }

    public void Delete()
    {
        if (DeleteFailure is not null)
            throw DeleteFailure;
        Secret = null;
        Marker = false;
    }
}
