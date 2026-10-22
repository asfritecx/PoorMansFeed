using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;
using GraphiteUi;

namespace PoorMansFeed.Tests;

/// <summary>
/// Guards the house style. The first group reads the .axaml sources, so a hex colour or an inline font size in a view
/// fails the build instead of reaching review. The second group checks the kit's behaviour in a real headless window.
/// </summary>
public sealed partial class HouseStyleTests
{
    // The GraphiteUi folder and App.axaml (the fixed accent colour) are the only places allowed to define raw values
    private static readonly string[] AllowedRawValueFiles = ["Theme.axaml", "App.axaml"];

    [GeneratedRegex("#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\\b")]
    private static partial Regex HexColour();

    [Fact]
    public void ViewsTakeColoursFromTheThemeNotFromHexValues()
    {
        var offenders = ViewFiles()
            .Where(file => HexColour().IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "Raw hex colours in: " + string.Join(", ", offenders) +
            ". Add a brush to GraphiteUi/Theme.axaml or use a Classes= style instead.");
    }

    [Fact]
    public void ViewsTakeTextSizeAndWeightFromThemeClasses()
    {
        var offenders = ViewFiles()
            .Where(file => XDocument.Load(file).Descendants().Any(element =>
                element.Attributes().Any(attribute => attribute.Name.LocalName is "FontSize" or "FontWeight")))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "Inline FontSize or FontWeight in: " + string.Join(", ", offenders) +
            ". Use Classes=\"title\", \"subtitle\", \"card-title\", \"section-title\", \"body-strong\", \"caption\", \"small\" or \"mono\".");
    }

    [Fact]
    public void ButtonsWithOnlyAnIconHaveAnAccessibleName()
    {
        // KitFiles() is every .axaml in the UI project, so this covers the views and the kit's own dialogs once each
        var offenders = KitFiles()
            .SelectMany(file => IconOnlyButtonsWithoutName(Path.GetFileName(file), XDocument.Load(file)))
            .ToList();

        Assert.True(offenders.Count == 0, "Icon-only buttons without AutomationProperties.Name: " + string.Join("; ", offenders));
    }

    [Fact]
    public void TheIconOnlyButtonCheckNamesAnOffenderInsteadOfThrowing()
    {
        const string xaml = """
            <StackPanel xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Button x:Name="RefreshButton"><LineIcon /></Button>
              <Button><LineIcon /></Button>
              <Button AutomationProperties.Name="Close"><LineIcon /></Button>
              <Button Content="Save" />
            </StackPanel>
            """;

        List<string> offenders = IconOnlyButtonsWithoutName("Sample.axaml", XDocument.Parse(xaml)).ToList();

        Assert.Equal(["Sample.axaml: RefreshButton", "Sample.axaml: (unnamed button)"], offenders);
    }

    [Fact]
    public void CSharpSourceWritesNonAsciiCharactersAsUnicodeEscapes()
    {
        // Every project and the tests, not only the UI: a raw character in test data can be lost to a re-save in another
        // encoding while the test still passes
        string solution = SolutionDirectory();
        var offenders = Directory
            .EnumerateFiles(solution, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(solution, file))
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" || part.StartsWith('.')))
            .Where(file => File.ReadAllText(Path.Combine(solution, file)).Any(character => character > 127))
            .ToList();

        Assert.True(offenders.Count == 0, "Raw non-ASCII characters in: " + string.Join(", ", offenders) +
            ". Write them as a unicode escape (backslash, u, four hex digits) so the source stays plain ASCII.");
    }

    [GeneratedRegex(@"\b(?:standing\s+)?orders?[ \t]+#?\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrderNumber();

    // The files at the top of the solution that are published with the source
    private static readonly string[] PublishedTopFiles =
        ["README.md", "Directory.Build.props", "global.json", "nuget.config", "PoorMansFeed.slnx", "build/publish.sh", "build/publish.ps1"];

    [Fact]
    public void SourceStatesTheRuleNotAnOrderNumber()
    {
        // The numbered instructions a change was made under stay out of the published files: an order's number means
        // nothing to a reader of the code, the tests or the readme, so the text states the rule itself. A number
        // wrapped onto the next line isn't seen
        string solution = SolutionDirectory();
        var offenders = new[] { "src", "tests" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(solution, folder), "*", SearchOption.AllDirectories))
            .Where(file => Path.GetExtension(file) is ".cs" or ".axaml" or ".csproj" or ".json" or ".manifest")
            .Concat(PublishedTopFiles.Select(file => Path.Combine(solution, file)).Where(File.Exists))
            .Select(file => Path.GetRelativePath(solution, file))
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" || part.StartsWith('.')))
            .SelectMany(file => File.ReadLines(Path.Combine(solution, file))
                .Select((line, index) => (line, number: index + 1))
                .Where(entry => OrderNumber().IsMatch(entry.line))
                .Select(entry => $"{file}:{entry.number}"))
            .ToList();

        Assert.True(offenders.Count == 0, "Order numbers in: " + string.Join(", ", offenders) +
            ". State the rule itself instead of the number it was given.");
    }

    [AvaloniaFact]
    public void EscapeCancelsAConfirmationAndEnterAloneNeverConfirms()
    {
        var owner = new Window();
        owner.Show();
        var dialog = new ConfirmationDialog("Delete files?", "Message", "Delete all files");
        Task<bool> result = dialog.ShowDialog<bool>(owner);

        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.False(result.IsCompletedSuccessfully && result.Result);

        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(result.IsCompleted);
        Assert.False(result.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public void DialogTitlesUseTheSubtitleClass()
    {
        var confirmation = new ConfirmationDialog("Delete files?", "Message", "Delete all files");
        var about = new AboutDialog();
        confirmation.Show();
        about.Show();

        TextBlock aboutTitle = about.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("subtitle"));
        Assert.Equal(20, confirmation.FindControl<TextBlock>("TitleText")!.FontSize);
        Assert.Equal(20, aboutTitle.FontSize);
        Assert.Equal(AppInfo.Name, aboutTitle.Text);
        Assert.Equal(AppInfo.AboutTitle, about.Title);
        confirmation.Close();
        about.Close();

        // The app's own dialogs keep their title in a TextBlock named TitleText, at the subtitle size
        foreach (Window dialog in AppDialogs())
        {
            dialog.Show();
            TextBlock title = dialog.FindControl<TextBlock>("TitleText")!;
            Assert.Contains("subtitle", title.Classes);
            Assert.Equal(20, title.FontSize);
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void DialogButtonsCentreTheirLabels()
    {
        // Fluent stretches a button's content, so without the kit's Button style a label sits at the left of an 88px button
        var confirmation = new ConfirmationDialog("Delete files?", "Message", "Delete");
        var about = new AboutDialog();
        foreach (Window dialog in new Window[] { confirmation, about }.Concat(AppDialogs()))
        {
            dialog.Show();
            // An icon-only button has no label to centre; its icon is checked by the automation-name sweep instead
            List<Button> buttons = dialog.GetVisualDescendants().OfType<Button>()
                .Where(button => button.IsVisible && button.GetType() == typeof(Button))
                .Where(button => button.GetVisualDescendants().OfType<TextBlock>().Any()).ToList();
            Assert.NotEmpty(buttons);
            Assert.All(buttons, button =>
            {
                TextBlock label = button.GetVisualDescendants().OfType<TextBlock>().First();
                Point? centre = label.TranslatePoint(new Point(label.Bounds.Width / 2, 0), button);
                Assert.NotNull(centre);
                Assert.InRange(centre.Value.X, (button.Bounds.Width / 2) - 1, (button.Bounds.Width / 2) + 1);
            });
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void APlaceholderKeepsItsSpaceButHidesAndBlocksInput()
    {
        var shown = new StackPanel { Height = 40 };
        var hidden = new StackPanel { Height = 40 };
        hidden.Classes.Add("placeholder");
        var window = new Window { Content = new StackPanel { Children = { shown, hidden } } };
        window.Show();

        Assert.Equal(40, hidden.Bounds.Height);
        Assert.Equal(0, hidden.Opacity);
        Assert.False(hidden.IsHitTestVisible);
        Assert.Equal(1, shown.Opacity);
        window.Close();
    }

    [AvaloniaFact]
    public void TheMainWindowShowsTheBuildNameVersionAndAttribution()
    {
        using var viewModel = new MainViewModel(new FakeIpInfoService(), new FakeUi());
        var window = new MainWindow(viewModel);
        window.Show();

        Assert.Equal(AppInfo.Title, window.Title);
        Assert.Equal(AppInfo.VersionLabel, window.FindControl<TextBlock>("VersionLabel")!.Text);
        Assert.Equal(AppText.Attribution, window.FindControl<TextBlock>("FooterNotice")!.Text);
        window.Close();
    }

    // The app's own dialogs, built the way the app builds them. Each is built here and never shown outside the test.
    private static IEnumerable<Window> AppDialogs()
    {
        yield return new TokenDialog(TokenPromptReason.FirstRun);
        yield return new TokenDialog(SecretPrompt.ForAbuseIpDb(AbuseKeyAsk.Missing));
        yield return new TokenDialog(SecretPrompt.ForAbuseIpDb(AbuseKeyAsk.Rejected));
        yield return new TokenDialog(SecretPrompt.ForAbuseIpDb(AbuseKeyAsk.Replace));
        yield return new LargeRangesDialog(new LargeRangePrompt("Export AS15169?", "Message",
            [new LargeRangeChoice("10.0.0.0/8", "16,777,216 addresses")]));
        PublicAddress.TryCreate([8, 8, 8, 8], out PublicAddress? address);
        yield return new NewAddressesDialog(new NewAddressesPrompt("New addresses", "Message", "1 new address",
            [new NewAddressRow("8.8.8.8", "100", "2026-10-19", WebPage.IpInfoAddress(address!), WebPage.AbuseIpDbCheck(address!))],
            "8.8.8.8", "Copied 1 address"),
            _ => Task.CompletedTask, _ => Task.CompletedTask);
        yield return new HelpDialog();
    }

    // x:Name lives in the XAML namespace, so it has to be looked up as a namespaced name: Attribute("x:Name") throws an
    // XmlException (a colon is not allowed in an XName), and it would do so exactly when there is an offender to report
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static IEnumerable<string> IconOnlyButtonsWithoutName(string fileName, XDocument document)
    {
        foreach (XElement button in document.Descendants().Where(e => e.Name.LocalName is "Button" or "HyperlinkButton"))
        {
            bool hasLabel = button.Attribute("Content") is not null
                || button.Descendants().Any(e => e.Name.LocalName is "TextBlock" or "TextBox");
            bool hasIcon = button.Descendants().Any(e => e.Name.LocalName == "LineIcon");
            bool hasName = button.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name");
            if (hasIcon && !hasLabel && !hasName)
            {
                yield return $"{fileName}: {button.Attribute(XamlNamespace + "Name")?.Value ?? "(unnamed button)"}";
            }
        }
    }

    // The view sources: every .axaml in the UI project except the theme, the app file and build output
    private static IEnumerable<string> ViewFiles() => KitFiles()
        .Where(file => !AllowedRawValueFiles.Contains(Path.GetFileName(file)))
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}GraphiteUi{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> KitFiles() => Directory
        .EnumerateFiles(UiProjectDirectory(), "*.axaml", SearchOption.AllDirectories)
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    // The folder that contains GraphiteUi/Theme.axaml, so the tests need no per-project path
    private static string UiProjectDirectory()
    {
        string theme = Directory
            .EnumerateFiles(SolutionDirectory(), "Theme.axaml", SearchOption.AllDirectories)
            .First(file => Path.GetFileName(Path.GetDirectoryName(file)) == "GraphiteUi"
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        return Path.GetDirectoryName(Path.GetDirectoryName(theme))!;
    }

    // Walks up from this file to the folder holding the solution
    private static string SolutionDirectory([CallerFilePath] string testFile = "")
    {
        for (DirectoryInfo? directory = new FileInfo(testFile).Directory; directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.sln*").Any())
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("No solution file found above " + testFile);
    }
}
