using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PoorMansFeed.Tests;

/// <summary>
/// Sweeps the sources for the rules that keep the API token and the AbuseIPDB key in the operating system's store:
/// neither is ever encrypted by the app, each platform library is used in one file, and no test reaches the real store.
/// The only cipher is config.db's sealing in ConfigSeal.cs, under a random key that is itself kept in the operating
/// system's store; there is no cipher, key derivation or passphrase anywhere else.
/// </summary>
public sealed class SecretHandlingTests
{
    // The token and the AbuseIPDB key are protected by DPAPI or the Keychain, never by the app. A cipher or key
    // derivation here would mean a key or passphrase in the app, which anyone with the source or the binary can read.
    // The one cipher is config.db's AES-GCM sealing, whose random key is kept by the OS store; it is fenced to its file
    // by SealingOnly, and every name below stays banned there too.
    private static readonly string[] OwnCryptography =
    [
        "Aes.Create", "AesCcm", "AesCng", "AesManaged", "AesCryptoServiceProvider", "Rijndael",
        "ChaCha20Poly1305", "TripleDES", "DESCryptoServiceProvider", "DES.Create", "RC2", "SymmetricAlgorithm",
        "EncryptCbc", "DecryptCbc", "EncryptEcb", "DecryptEcb", "RSA.Create", "RSACryptoServiceProvider", "HKDF",
        "Rfc2898DeriveBytes", "PasswordDeriveBytes", "Pbkdf2", "CreateEncryptor", "CreateDecryptor", "ICryptoTransform",
        "CryptoStream", "DataProtectionProvider", "IDataProtector",
    ];

    // The sealing names and the one file that may use each. CryptographicOperations (ZeroMemory) is not swept.
    private static readonly (string Name, string OnlyFile)[] SealingOnly =
    [
        ("AesGcm", "ConfigSeal.cs"),
        ("RandomNumberGenerator", "ConfigSeal.cs"),
    ];

    // Calls that turn bytes into text, which no trace or exception in the config key's files may hold
    private static readonly string[] BytesToText = ["Convert.To", "BitConverter", "Encoding.", "GetString", "Join("];

    // What makes a store that holds the developer's own token
    private static readonly string[] RealStores =
    [
        "IpInfoService.ForCurrentUser(", "SecretStores.ForCurrentUser(", "SecretStores.AbuseIpDbKeyForCurrentUser(",
        "SecretStores.ConfigKeyForCurrentUser(", "new KeychainSecretStore(", "new DpapiSecretStore(",
        "AbuseIpDbService.ForCurrentUser(",
    ];

    [Fact]
    public void No_source_file_encrypts_or_derives_a_key_itself()
    {
        var offenders = SourceFiles("src")
            .SelectMany(file => OwnCryptography
                .Where(name => File.ReadAllText(file).Contains(name, StringComparison.Ordinal))
                .Select(name => $"{Path.GetFileName(file)}: {name}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Sealing_names_are_used_in_one_file()
    {
        foreach ((string name, string onlyFile) in SealingOnly)
        {
            var users = SourceFiles("src")
                .Where(file => File.ReadAllText(file).Contains(name, StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();

            Assert.Equal([onlyFile], users);
        }
    }

    [Fact]
    public void Config_paths_are_named_only_where_they_belong()
    {
        // On macOS a System.IO open of config.db while the app's SQLite connection holds it drops SQLite's locks for
        // the whole process, so only the store may touch the file. On Windows the key's file is all that tells the
        // secrets apart, so only its store factory may name it.
        List<string> Naming(string pattern) => SourceFiles("src")
            .Where(file => Regex.IsMatch(File.ReadAllText(file), pattern))
            .Select(file => Path.GetFileName(file))
            .Order(StringComparer.Ordinal)
            .ToList();

        string root = RepositoryRoot();
        List<string> Users(string pattern) => SourceFiles("src")
            .Where(file => Regex.IsMatch(File.ReadAllText(file), pattern))
            .Select(file => Path.GetRelativePath(Path.Combine(root, "src", "PoorMansFeed.Core"), file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Only the store and AppPaths name a path to the file. The bare file names may also appear in the UI's texts,
        // which name the file to the user and open nothing.
        Assert.Equal(["AppPaths.cs", "Config/ConfigStore.cs"],
                     Users(@"\bConfigPath\b|\bUnreadableConfigPath\b|\bConfigMadePath\b|\bConfigMadeFileName\b"));
        Assert.Equal(["../PoorMansFeed/AppText.cs", "AppPaths.cs"], Users(@"\bConfigFileName\b|\bUnreadableConfigFileName\b"));
        Assert.DoesNotMatch(AppTextFileAccess, File.ReadAllText(Path.Combine(root, "src", "PoorMansFeed", "AppText.cs")));

        Assert.Equal(["AppPaths.cs", "SecretStores.cs"], Naming(@"\bConfigKeyPath\b"));
    }

    // Any way of opening, listing, running or importing a file API, which the UI's texts must never hold. The type names
    // are refused anywhere, so a target-typed new(...) is caught too. File and Directory are refused before a dot, with
    // any white space between, and so is any using alias, which could rename them
    private const string AppTextFileAccess =
        @"\b(File|Directory)\s*\.|\b(FileStream|FileInfo|DirectoryInfo|StreamReader|StreamWriter|SqliteConnection" +
        @"|Process|ProcessStartInfo|FileSystemWatcher)\b" +
        @"|\bPath\.(Combine|Join)\b|\busing\s+static\b|\busing\s+\w+\s*=";

    [Theory]
    [InlineData("File.Open(path)")]
    [InlineData("Directory.Exists(path)")]
    [InlineData("Process.Start(path)")]
    [InlineData("new FileStream(path, FileMode.Open)")]
    [InlineData("new FileInfo(path)")]
    [InlineData("new DirectoryInfo(path)")]
    [InlineData("new StreamReader(path)")]
    [InlineData("new  StreamWriter(path)")]
    [InlineData("new SqliteConnection(text)")]
    [InlineData("FileStream s = new(path, FileMode.Open);")]
    [InlineData("StreamReader r = new(p);")]
    [InlineData("Process p = new() { StartInfo = { FileName = path } };")]
    [InlineData("ProcessStartInfo i = new(path);")]
    [InlineData("new FileSystemWatcher(path)")]
    [InlineData("System.IO.File.ReadAllText(path)")]
    [InlineData("Path.Combine(a, b)")]
    [InlineData("Path.Join(a, b)")]
    [InlineData("using static System.IO.File;")]
    [InlineData("using F = System.IO.File;")]
    [InlineData("File\n    .ReadAllText(path)")]
    public void The_AppText_guard_catches_each_file_access(string probe) => Assert.Matches(AppTextFileAccess, probe);

    [Fact]
    public void The_config_key_is_never_traced()
    {
        // Nothing may turn bytes into text inside a trace or an exception: the key, a read-back or a blob would follow
        IEnumerable<string> Offenders(string name, string text) =>
            new[] { "Trace.", "throw " }
                .SelectMany(opening => Statements(text, opening))
                .Where(statement => BytesToText.Any(call => statement.Text.Contains(call, StringComparison.Ordinal)))
                .Select(statement => $"{name}:{statement.Line}")
                .Distinct();

        // The sweep itself sees a banned call on a later line of a statement, and not one in the statement after it
        Assert.Equal(new[] { "Sample.cs:2" }, Offenders("Sample.cs",
            "int a = 1;\nTrace.TraceWarning($\"x {\n    Convert.ToHexString(key)}\");\nint b = 2;").ToArray());
        Assert.Equal(new[] { "Sample.cs:1" }, Offenders("Sample.cs",
            "throw new ArgumentException(\"a;b\" +\n    BitConverter.ToString(key));").ToArray());
        Assert.Empty(Offenders("Sample.cs", "Trace.TraceWarning(\"a;b\");\nUse(Convert.ToBase64String(key));"));

        string folder = Path.Combine(RepositoryRoot(), "src", "PoorMansFeed.Core", "Config");
        string[] files = [Path.Combine(folder, "ConfigSeal.cs"), Path.Combine(folder, "ConfigKeyStore.cs"),
                          Path.Combine(folder, "ConfigStore.cs")];
        Assert.All(files, file => Assert.True(File.Exists(file), file));

        var offenders = files.SelectMany(file => Offenders(Path.GetFileName(file), File.ReadAllText(file))).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ConfigSeal_has_no_fixed_nonce()
    {
        // A nonce must come from RandomNumberGenerator.Fill on every seal, never from a field or the stack
        string text = File.ReadAllText(SourceFiles("src").Single(file => Path.GetFileName(file) == "ConfigSeal.cs"));

        Assert.DoesNotContain("static readonly byte[]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stackalloc", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ProtectedData.", "DpapiSecretStore.cs")]
    [InlineData("Microsoft.Identity.Client", "KeychainSecretStore.cs")]
    [InlineData("SecretStores.ForCurrentUser(", "IpInfoService.cs")]
    [InlineData("SecretStores.ConfigKeyForCurrentUser(", "IpInfoService.cs")]
    [InlineData("SecretStores.AbuseIpDbKeyForCurrentUser(", "AbuseIpDbService.cs")]
    [InlineData("new AbuseIpDbService(", "AbuseIpDbService.cs")]
    [InlineData("new ConfigSeal(", "ConfigStore.cs")]
    public void Each_store_library_is_used_in_one_file(string usage, string onlyFile)
    {
        var users = SourceFiles("src")
            .Where(file => File.ReadAllText(file).Contains(usage, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal([onlyFile], users);
    }

    [Fact]
    public void The_AbuseIPDB_service_makes_its_own_client_that_follows_no_redirect()
    {
        // .NET forwards a custom header such as Key when it follows a redirect to another host, so the key's client is
        // made in one place, over the handler with redirects off
        string text = File.ReadAllText(SourceFiles("src").Single(file => Path.GetFileName(file) == "AbuseIpDbService.cs"));

        Assert.Single(text.Split("new HttpClient(").Skip(1));
        Assert.Contains("new HttpClient(CreateHandler())", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Key_header_is_set_in_one_place()
    {
        var places = SourceFiles("src")
            .SelectMany(file => Enumerable.Repeat(Path.GetFileName(file),
                File.ReadAllText(file).Split("TryAddWithoutValidation(\"Key\"").Length - 1))
            .ToList();

        Assert.Equal(["AbuseIpDbClient.cs"], places);
    }

    [Fact]
    public void The_browser_is_asked_for_a_page_in_one_place()
    {
        // Only a WebPage reaches the browser, through the one https-checked call: Windows hands the launcher's text to
        // ShellExecute. So the call is made once, and no other way to start a program or open a link is used
        string root = RepositoryRoot();
        var calls = SourceFiles("src")
            .SelectMany(file => Enumerable.Repeat(
                Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                Regex.Count(File.ReadAllText(file), Regex.Escape("LaunchUriAsync"))))
            .ToList();

        Assert.Equal(["src/PoorMansFeed/AvaloniaUiServices.cs"], calls);

        // StartInfo also catches a ProcessStartInfo, and a Process started without a Process.Start call
        string[] otherLaunchers = ["Process.Start", "StartInfo", "UseShellExecute", "NavigateUri"];
        string separator = Path.DirectorySeparatorChar.ToString();
        var offenders = SourceFiles("src")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src"), "*.axaml", SearchOption.AllDirectories)
                .Where(file => !file.Contains(separator + "obj" + separator, StringComparison.Ordinal)
                    && !file.Contains(separator + "bin" + separator, StringComparison.Ordinal)))
            .Where(file => otherLaunchers.Any(name => File.ReadAllText(file).Contains(name, StringComparison.Ordinal)))
            .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_AbuseIPDB_trace_names_a_request_url()
    {
        // The check URL carries the address, and a traced URL is one step from a traced header
        string[] urls = ["RequestUri", "AbuseIpDbCheckUrl", "AbuseIpDbBlacklistUrl"];
        string folder = Path.Combine("src", "PoorMansFeed.Core", "AbuseIpDb");

        IEnumerable<string> Offenders(string name, string text) =>
            Statements(text, "Trace.")
                .Where(statement => urls.Any(url => statement.Text.Contains(url, StringComparison.Ordinal)))
                .Select(statement => $"{name}:{statement.Line}");

        // The sweep itself sees a URL on the second line of a call, and not one in the statement after it
        Assert.Equal(new[] { "Sample.cs:2" }, Offenders("Sample.cs",
            "int a = 1;\nTrace.TraceWarning(string.Create(CultureInfo.InvariantCulture,\n    $\"x; {request.RequestUri}\"));\nint b = 2;").ToArray());
        Assert.Empty(Offenders("Sample.cs", "Trace.TraceWarning(\"a;b\");\nUse(request.RequestUri);"));

        var offenders = SourceFiles(folder)
            .SelectMany(file => Offenders(Path.GetFileName(file), File.ReadAllText(file)))
            .ToList();

        Assert.NotEmpty(SourceFiles(folder));
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_rolling_list_holds_no_secret_and_no_hash()
    {
        // The list is the user's data, not a secret: it neither reads the key nor signs or hashes what it stores
        string text = File.ReadAllText(SourceFiles("src").Single(file => Path.GetFileName(file) == "AbuseIpDbList.cs"));

        foreach (string name in new[] { "TokenStore", "ISecretStore", "SHA256", "HMAC" })
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_list_file_is_named_only_by_the_paths_and_the_service()
    {
        var users = SourceFiles("src")
            .Where(file => File.ReadAllText(file).Contains("AbuseIpDbDatabasePath", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["AbuseIpDbService.cs", "AppPaths.cs"], users);
    }

    // Only AbuseIpDbFeedWriter.Select, which applies the exceptions, may make a feed family with addresses, so a
    // report's selection can never be written as a feed. In any other file that names the family or WriteFamily:
    // "new AbuseFeedFamily(" only with no addresses ([]); no declaration "AbuseFeedFamily x = new("; no target-typed
    // "new(" with four arguments unless its addresses are [] (a family's shape, wherever it stands: an argument, a
    // return, an expression body); no "with" on a name declared as a family; and no "with" of any receiver (a call,
    // an indexer) that sets one of the family's members.
    [Fact]
    public void A_feed_family_with_addresses_is_made_only_by_the_writer()
    {
        var offenders = new List<string>();
        foreach (string file in SourceFiles("src").Where(file => Path.GetFileName(file) != "AbuseIpDbFeedWriter.cs"))
        {
            string text = File.ReadAllText(file);
            if (!text.Contains("AbuseFeedFamily", StringComparison.Ordinal)
                && !text.Contains("WriteFamily(", StringComparison.Ordinal))
            {
                continue;
            }

            string name = Path.GetFileName(file);
            foreach (Match made in Regex.Matches(text, @"\bnew\s+AbuseFeedFamily\s*\("))
            {
                string addresses = AddressesArgument(ArgumentsFrom(text, made.Index + made.Length));
                if (addresses != "[]")
                {
                    offenders.Add($"{name}: new AbuseFeedFamily with addresses {addresses}");
                }
            }

            foreach (Match typed in Regex.Matches(text, @"\bAbuseFeedFamily\??\s+\w+\s*=\s*new\s*\("))
            {
                offenders.Add($"{name}: target-typed {typed.Value}");
            }

            // A target-typed new with the family's four arguments can't be told apart from a family, wherever it stands
            foreach (Match typed in Regex.Matches(text, @"\bnew\s*\("))
            {
                List<string> arguments = ArgumentsFrom(text, typed.Index + typed.Length);
                if (arguments.Count == 4 && AddressesArgument(arguments) != "[]")
                {
                    offenders.Add($"{name}: target-typed new({string.Join(", ", arguments)})");
                }
            }

            // A "with" on a name this file declares as a family is a copy
            var families = Regex.Matches(text, @"\bAbuseFeedFamily\??\s+(\w+)").Select(match => match.Groups[1].Value).ToHashSet();
            foreach (Match copy in Regex.Matches(text, @"(\w+)\s+with\s*\{([^}]*)\}"))
            {
                if (families.Contains(copy.Groups[1].Value))
                {
                    offenders.Add($"{name}: {copy.Value}");
                }
            }

            // So is a "with" of any receiver that sets one of the family's members
            foreach (Match copy in Regex.Matches(text, @"\bwith\s*\{([^}]*)\}"))
            {
                if (Regex.IsMatch(copy.Groups[1].Value, @"\b(IsIpv6|Addresses|ExceptionRemovals|LeftOut)\s*="))
                {
                    offenders.Add($"{name}: {copy.Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    // A family's addresses argument: the one named Addresses: if there is one, else the second
    private static string AddressesArgument(List<string> arguments) =>
        arguments.FirstOrDefault(argument => argument.StartsWith("Addresses:", StringComparison.Ordinal)) is { } named
            ? named["Addresses:".Length..].Trim()
            : arguments.Count > 1 ? arguments[1] : "";

    // The top-level arguments of a call whose opening parenthesis ends just before start, each trimmed
    private static List<string> ArgumentsFrom(string text, int start)
    {
        var arguments = new List<string>();
        int depth = 0;
        int from = start;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    arguments.Add(text[from..i].Trim());
                    return arguments;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(text[from..i].Trim());
                from = i + 1;
            }
        }

        return arguments;
    }

    [Fact]
    public void Tests_never_make_a_real_secret_store()
    {
        // The two platform store tests use throwaway files and Keychain names of their own
        string[] allowed = [Path.GetFileName(ThisFile()), "DpapiSecretStoreTests.cs", "KeychainSecretStoreTests.cs"];
        var offenders = SourceFiles("tests")
            .Where(file => !allowed.Contains(Path.GetFileName(file)))
            .SelectMany(file => RealStores
                .Where(call => File.ReadAllText(file).Contains(call, StringComparison.Ordinal))
                .Select(call => $"{Path.GetFileName(file)}: {call}"))
            .ToList();

        Assert.Empty(offenders);
    }

    // Each statement that starts at the opening text is read to the ';' that ends it, so a name on a later line counts
    // too. A ';' inside a string literal doesn't end it. The reading is plain text, not C#, so a statement ends early,
    // and a name after that point goes unseen, at a ';' in a '//' comment, at a char literal (';' or '"'), or at a string
    // inside an interpolation hole, whose quotes flip the string state
    private static IEnumerable<(string Text, int Line)> Statements(string text, string opening)
    {
        for (int start = text.IndexOf(opening, StringComparison.Ordinal); start >= 0;
             start = text.IndexOf(opening, start + 1, StringComparison.Ordinal))
        {
            int end = start;
            bool quoted = false;
            for (; end < text.Length && (quoted || text[end] != ';'); end++)
            {
                if (text[end] == '\\' && quoted)
                    end++;
                else if (text[end] == '"')
                    quoted = !quoted;
            }

            yield return (text[start..Math.Min(end, text.Length)], text.AsSpan(0, start).Count('\n') + 1);
        }
    }

    private static IEnumerable<string> SourceFiles(string folder)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), folder), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(separator + "obj" + separator, StringComparison.Ordinal)
                && !file.Contains(separator + "bin" + separator, StringComparison.Ordinal));
    }

    private static string ThisFile([CallerFilePath] string testFile = "") => testFile;

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new FileInfo(ThisFile()).Directory; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PoorMansFeed.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("The repository root wasn't found.");
    }
}
