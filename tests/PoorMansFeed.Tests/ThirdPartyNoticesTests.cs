using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoorMansFeed.Tests;

/// <summary>
/// Keeps THIRD-PARTY-NOTICES.txt in step with the packages the release builds carry, so a package update that brings
/// in a new dependency fails here instead of shipping without its licence.
/// </summary>
public sealed partial class ThirdPartyNoticesTests
{
    // Locked packages that the Windows and macOS release builds don't carry: build-time tooling and other platforms
    private static readonly string[] NotShipped = ["Avalonia.BuildServices"];
    private static readonly string[] NotShippedSuffixes = [".NativeAssets.Linux", ".NativeAssets.WebAssembly"];

    // A "Packages:" line in part 1 and its indented continuation lines
    [GeneratedRegex(@"^Packages: (.+(?:\n  .+)*)", RegexOptions.Multiline)]
    private static partial Regex PackagesLine();

    [Fact]
    public void EveryShippedPackageIsInTheNotices()
    {
        Assert.Empty(ShippedPackages().Except(NoticedPackages()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheNoticesListOnlyShippedPackages()
    {
        Assert.Empty(NoticedPackages().Except(ShippedPackages()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheLicenceIsMit()
    {
        string licence = File.ReadAllText(Path.Combine(RepositoryRoot(), "LICENSE"));

        Assert.StartsWith("MIT License", licence, StringComparison.Ordinal);
        Assert.Contains("THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND", licence, StringComparison.Ordinal);
    }

    private static HashSet<string> ShippedPackages()
    {
        string lockFile = Path.Combine(RepositoryRoot(), "src", "PoorMansFeed", "packages.lock.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(lockFile));

        // The framework group (net10.0) lists every package; the runtime groups (net10.0/osx-arm64) repeat some
        return document.RootElement.GetProperty("dependencies").EnumerateObject()
            .Single(group => !group.Name.Contains('/'))
            .Value.EnumerateObject()
            .Where(package => package.Value.GetProperty("type").GetString() != "Project")
            .Select(package => package.Name)
            .Where(name => !NotShipped.Contains(name)
                && !NotShippedSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> NoticedPackages()
    {
        string notices = File.ReadAllText(Path.Combine(RepositoryRoot(), "THIRD-PARTY-NOTICES.txt")).ReplaceLineEndings("\n");
        string partOne = notices[..notices.IndexOf("\nPart 2: ", StringComparison.Ordinal)];

        return PackagesLine().Matches(partOne)
            .SelectMany(match => match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
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
