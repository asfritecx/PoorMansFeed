using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Core.Config;

/// <summary>What opening config.db gave. Only <see cref="Opened"/> comes with a store.</summary>
internal enum ConfigOpenState
{
    /// <summary>The file is open, checked and held under the exclusive lock.</summary>
    Opened,

    /// <summary>A newer version wrote the file. Nothing past its schema version was read, and the key wasn't.</summary>
    Newer,

    /// <summary>The file isn't a database, is damaged, isn't this application's config file, or holds a value that
    /// can't be read.</summary>
    Unreadable,

    /// <summary>The sections don't open with the saved key: no key, a wrong or damaged one, or a section changed,
    /// added, removed or copied outside the app.</summary>
    KeyMismatch,

    /// <summary>Another program holds the file.</summary>
    Locked,

    /// <summary>The file or its folder can't be opened or written now.</summary>
    StorageError,

    /// <summary>The key's store can't be reached now (a locked keychain, a denied prompt), or a new key didn't save.</summary>
    KeyUnavailable,

    /// <summary>This system has no store the app can keep the key in.</summary>
    NoKeyStore,

    /// <summary>The exceptions.json to import can't be read, or couldn't be set aside. Nothing was written.</summary>
    ExceptionsImportUnreadable,

    /// <summary>config.db is gone or empty though an earlier launch made one (the config_made marker or a *.migrated file
    /// is there), whatever settings.json or exceptions.json are beside it. Nothing was made or imported: a new file would
    /// silently drop the exceptions.</summary>
    Missing,
}

/// <summary>The values config.db holds, as last committed.</summary>
internal sealed record ConfigSnapshot(UserSettings Settings, ExceptionList Exceptions, AbuseAccounting Accounting);
