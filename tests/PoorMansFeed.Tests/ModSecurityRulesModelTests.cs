using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>The rules dialog's model: each build outcome's preview, the action, the SAS expiry and Copy.</summary>
public sealed class ModSecurityRulesModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
    private const string Folder = "/etc/modsecurity/lists";
    private const string Blob = "https://example.blob.core.windows.net/lists";
    private static readonly string[] Files = ["AS15169_ipv4.txt", "AS15169_ipv6.txt"];

    private static ModSecurityRulesPrompt Prompt(string location = Folder,
        ModSecurityAction action = ModSecurityAction.LogOnly, string[]? files = null, int[]? ids = null) =>
        new(files ?? Files, ids ?? [10000, 10001], "AS15169", location, action);

    private static ModSecurityRulesModel Model(ModSecurityRulesPrompt prompt, TimeProvider? time = null,
        Func<string, Task>? copy = null) =>
        new(prompt, time ?? new FakeTimeProvider(Now), copy ?? (_ => Task.CompletedTask));

    private static string Sas(string query) => Blob + "?" + query;

    private static string BuildText(ModSecurityRulesPrompt prompt, string location, ModSecurityAction action) =>
        ModSecurityRules.Build(new ModSecurityRuleRequest(prompt.RuleIds, action, location, prompt.FileNames,
            prompt.FeedLabel)).Text;

    private static string LocationLine(string location) => AppText.ModSecurityLocationNoteFor(location, Now).Text;

    // One prompt per failure; a failure added later has no row and fails the coverage test
    private static readonly Dictionary<ModSecurityRuleFailure, (ModSecurityRulesPrompt Prompt, Func<string> Preview)> Cases =
        new()
        {
            [ModSecurityRuleFailure.None] = (Prompt(), () => BuildText(Prompt(), Folder, ModSecurityAction.LogOnly)),
            [ModSecurityRuleFailure.NoFiles] = (Prompt(files: [], ids: []), () => AppText.Generic),
            [ModSecurityRuleFailure.NoLocation] = (Prompt("   "), () => AppText.ModSecurityRulesEnterLocation),
            [ModSecurityRuleFailure.LocationInvalid] = (Prompt("etc/lists"), () => LocationLine("etc/lists")),
            [ModSecurityRuleFailure.NotHttps] = (Prompt("http://example.com/lists"),
                () => LocationLine("http://example.com/lists")),
            [ModSecurityRuleFailure.FileNameInvalid] = (Prompt(files: ["bad name.txt", "AS1.txt"]), () => AppText.Generic),
            [ModSecurityRuleFailure.RuleIdOutOfRange] = (Prompt(ids: [0, 10001]), () => AppText.Generic),
            [ModSecurityRuleFailure.SasCanWrite] = (Prompt(Sas("sp=rw&sr=c&se=2026-12-31&sig=x")),
                () => LocationLine(Sas("sp=rw&sr=c&se=2026-12-31&sig=x"))),
            [ModSecurityRuleFailure.RuleIdRepeated] = (Prompt(ids: [10000, 10000]), () => AppText.Generic),
            [ModSecurityRuleFailure.SasRightsUnknown] = (Prompt(Sas("sr=c&se=2026-12-31&sig=x")),
                () => LocationLine(Sas("sr=c&se=2026-12-31&sig=x"))),
            [ModSecurityRuleFailure.SasScopeTooWide] = (Prompt(Sas("sp=r&sr=b&se=2026-12-31&sig=x")),
                () => LocationLine(Sas("sp=r&sr=b&se=2026-12-31&sig=x"))),
            [ModSecurityRuleFailure.SasExpiryUnknown] = (Prompt(Sas("sp=r&sr=c&sig=x")),
                () => LocationLine(Sas("sp=r&sr=c&sig=x"))),
        };

    [Fact]
    public void EveryFailureHasAPreviewCase()
    {
        Assert.Equal(Enum.GetValues<ModSecurityRuleFailure>().Order(), Cases.Keys.Order());
    }

    [Fact]
    public void EachCaseGivesItsFailure()
    {
        foreach ((ModSecurityRuleFailure failure, (ModSecurityRulesPrompt prompt, _)) in Cases)
        {
            ModSecurityRuleSet set = ModSecurityRules.Build(new ModSecurityRuleRequest(prompt.RuleIds, prompt.Action,
                prompt.Location.Trim(), prompt.FileNames, prompt.FeedLabel));
            Assert.Equal(failure, set.Failure);
        }
    }

    [Fact]
    public void EveryFailureShowsItsPreview()
    {
        foreach ((ModSecurityRuleFailure failure, (ModSecurityRulesPrompt prompt, Func<string> preview)) in Cases)
        {
            ModSecurityRulesModel model = Model(prompt);
            Assert.Equal(preview(), model.PreviewText);
            Assert.False(string.IsNullOrEmpty(model.PreviewText));
            bool ok = failure == ModSecurityRuleFailure.None;
            Assert.Equal(ok, model.HasRules);
            Assert.Equal(!ok, model.IsProblem);
            Assert.Equal(ok, model.CopyCommand.CanExecute(null));
        }
    }

    [Fact]
    public void TheRulesAreTheBuildText()
    {
        ModSecurityRulesModel model = Model(Prompt());

        Assert.Equal(BuildText(Prompt(), Folder, ModSecurityAction.LogOnly), model.PreviewText);
        Assert.EndsWith("\n", model.PreviewText);
        Assert.Contains("pass", model.PreviewText);
        Assert.Equal(AppText.ModSecurityRulesIds(Files, [10000, 10001]), model.IdsText);
    }

    [Fact]
    public void TheLocationIsTrimmed()
    {
        ModSecurityRulesModel model = Model(Prompt());
        string trimmed = model.PreviewText;

        model.Location = "  " + Folder + "  ";

        Assert.Equal("  " + Folder + "  ", model.Location);
        Assert.Equal(trimmed, model.PreviewText);
        Assert.True(model.HasRules);
    }

    [Fact]
    public void ANullLocationIsEmpty()
    {
        ModSecurityRulesModel model = Model(Prompt());

        model.Location = null!;

        Assert.Equal("", model.Location);
        Assert.Equal(AppText.ModSecurityRulesEnterLocation, model.PreviewText);
    }

    [Fact]
    public void TheActionStartsFromThePromptAndOnlyTrueChangesIt()
    {
        ModSecurityRulesModel model = Model(Prompt());
        Assert.True(model.IsLogOnly);
        Assert.False(model.IsDeny);

        model.IsDeny = true;
        Assert.True(model.IsDeny);
        Assert.False(model.IsLogOnly);
        Assert.Equal(BuildText(Prompt(), Folder, ModSecurityAction.Deny), model.PreviewText);
        Assert.Contains("deny,status:403", model.PreviewText);

        model.IsDeny = false;
        Assert.True(model.IsDeny);
        model.IsLogOnly = false;
        Assert.True(model.IsDeny);

        model.IsLogOnly = true;
        Assert.True(model.IsLogOnly);
        Assert.Equal(BuildText(Prompt(), Folder, ModSecurityAction.LogOnly), model.PreviewText);

        Assert.True(Model(Prompt(action: ModSecurityAction.Deny)).IsDeny);
    }

    [Fact]
    public void CloseIsPickedOnlyByTrueAndBuildsTheDropRule()
    {
        ModSecurityRulesModel model = Model(Prompt());
        Assert.False(model.IsClose);

        model.IsClose = true;
        Assert.True(model.IsClose);
        Assert.False(model.IsLogOnly);
        Assert.False(model.IsDeny);
        Assert.Equal(BuildText(Prompt(), Folder, ModSecurityAction.Close), model.PreviewText);
        Assert.Contains("drop,status:444", model.PreviewText);

        model.IsClose = false;
        Assert.True(model.IsClose);
        model.IsDeny = false;
        Assert.True(model.IsClose);

        model.IsDeny = true;
        Assert.False(model.IsClose);
        Assert.Equal(BuildText(Prompt(), Folder, ModSecurityAction.Deny), model.PreviewText);

        ModSecurityRulesModel saved = Model(Prompt(action: ModSecurityAction.Close));
        Assert.True(saved.IsClose);
        Assert.False(saved.IsLogOnly);
    }

    [Fact]
    public void AFolderHasNoExpiry()
    {
        ModSecurityRulesModel model = Model(Prompt());

        Assert.Equal("", model.ExpiryText);
        Assert.False(model.HasExpiry);
        Assert.False(model.IsExpiryCaution);
        Assert.False(model.IsExpiryCritical);
    }

    [Theory]
    [InlineData("2026-12-31", false, false)]
    [InlineData("2026-10-30", true, false)]
    [InlineData("2026-10-01", false, true)]
    public void TheExpiryTone(string expiry, bool caution, bool critical)
    {
        string link = Sas("sp=r&sr=c&se=" + expiry + "&sig=x");
        ModSecurityRulesModel model = Model(Prompt(link));

        Assert.True(model.HasRules);
        Assert.Equal(LocationLine(link), model.ExpiryText);
        Assert.True(model.HasExpiry);
        Assert.Equal(caution, model.IsExpiryCaution);
        Assert.Equal(critical, model.IsExpiryCritical);
    }

    [Fact]
    public void ARefusedLinkShowsItsProblemInThePreviewOnly()
    {
        string link = Sas("sp=rw&sr=c&se=2026-10-01&sig=x");
        ModSecurityRulesModel model = Model(Prompt(link));

        Assert.Equal(LocationLine(link), model.PreviewText);
        Assert.True(model.IsProblem);
        Assert.Equal("", model.ExpiryText);
        Assert.False(model.HasExpiry);
        Assert.False(model.IsExpiryCritical);
        Assert.False(model.IsExpiryCaution);
    }

    [Fact]
    public void ARefusalShowsBeforeAnIdFailure()
    {
        const string relative = "etc/lists";
        ModSecurityRulesModel model = Model(Prompt(relative, ids: [10000, 10000]));

        Assert.Equal(LocationLine(relative), model.PreviewText);
        Assert.Equal("", model.ExpiryText);
        Assert.False(model.HasExpiry);

        model.Location = Folder;
        Assert.Equal(AppText.Generic, model.PreviewText);
        Assert.True(model.IsProblem);
    }

    [Fact]
    public void TheClockIsReadAtEachEdit()
    {
        var time = new FakeTimeProvider(Now);
        string link = Sas("sp=r&sr=c&se=2026-11-30&sig=x");
        ModSecurityRulesModel model = Model(Prompt(link), time);
        Assert.False(model.IsExpiryCaution);

        time.Advance(TimeSpan.FromDays(30));
        model.Location = link + " ";
        Assert.True(model.IsExpiryCaution);

        time.Advance(TimeSpan.FromDays(10));
        model.Location = link;
        Assert.True(model.IsExpiryCritical);
        Assert.False(model.IsExpiryCaution);
    }

    [Fact]
    public async Task CopyCopiesTheWholeTextAndSaysHowManyRules()
    {
        var copied = new List<string>();
        ModSecurityRulesModel model = Model(Prompt(), copy: text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        });

        await model.CopyCommand.ExecuteAsync(null);

        Assert.Equal([BuildText(Prompt(), Folder, ModSecurityAction.LogOnly)], copied);
        Assert.Equal("Copied 2 rules", model.CopyStatusText);
        Assert.True(model.HasCopyStatus);

        ModSecurityRulesModel one = Model(Prompt(files: ["AS1.txt"], ids: [10000]));
        await one.CopyCommand.ExecuteAsync(null);
        Assert.Equal("Copied 1 rule", one.CopyStatusText);
    }

    [Fact]
    public async Task ACopyThatThrowsSaysSo()
    {
        ModSecurityRulesModel model = Model(Prompt(), copy: _ => throw new InvalidOperationException("no clipboard"));

        await model.CopyCommand.ExecuteAsync(null);

        Assert.Equal(AppText.ModSecurityRulesCopyFailed, model.CopyStatusText);
    }

    [Fact]
    public async Task AnEditThatChangesTheRulesClearsTheCopyStatus()
    {
        ModSecurityRulesModel model = Model(Prompt());
        await model.CopyCommand.ExecuteAsync(null);

        model.Location = " " + Folder;
        Assert.True(model.HasCopyStatus);

        model.IsDeny = true;
        Assert.Equal("", model.CopyStatusText);
        Assert.False(model.HasCopyStatus);
    }

    [Fact]
    public async Task ACopyOvertakenByAnEditSaysNothing()
    {
        var held = new TaskCompletionSource();
        ModSecurityRulesModel model = Model(Prompt(), copy: _ => held.Task);

        Task copy = model.CopyCommand.ExecuteAsync(null);
        model.IsDeny = true;
        held.SetResult();
        await copy;
        Assert.Equal("", model.CopyStatusText);

        // Back to the rules that were copied, the line is true again
        var again = new TaskCompletionSource();
        ModSecurityRulesModel back = Model(Prompt(), copy: _ => again.Task);
        Task second = back.CopyCommand.ExecuteAsync(null);
        back.IsDeny = true;
        back.IsLogOnly = true;
        again.SetResult();
        await second;
        Assert.Equal("Copied 2 rules", back.CopyStatusText);
    }

    [Fact]
    public async Task AFailedCopyOvertakenByAnEditSaysNothing()
    {
        var held = new TaskCompletionSource();
        ModSecurityRulesModel model = Model(Prompt(), copy: _ => held.Task);

        Task copy = model.CopyCommand.ExecuteAsync(null);
        model.Location = "/srv/lists";
        held.SetException(new InvalidOperationException("no clipboard"));
        await copy;
        Assert.Equal("", model.CopyStatusText);
    }

    [Fact]
    public void EditsRaiseTheirChanges()
    {
        ModSecurityRulesModel model = Model(Prompt());
        var changed = new List<string?>();
        int canExecute = 0;
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.CopyCommand.CanExecuteChanged += (_, _) => canExecute++;

        model.Location = "";

        Assert.Contains(nameof(ModSecurityRulesModel.Location), changed);
        Assert.Contains(nameof(ModSecurityRulesModel.PreviewText), changed);
        Assert.Contains(nameof(ModSecurityRulesModel.HasRules), changed);
        Assert.Contains(nameof(ModSecurityRulesModel.IsProblem), changed);
        Assert.Equal(1, canExecute);
        Assert.False(model.CopyCommand.CanExecute(null));

        changed.Clear();
        model.Location = Sas("sp=r&sr=c&se=2026-10-30&sig=x");
        Assert.Contains(nameof(ModSecurityRulesModel.ExpiryText), changed);
        Assert.Contains(nameof(ModSecurityRulesModel.HasExpiry), changed);
        Assert.Contains(nameof(ModSecurityRulesModel.IsExpiryCaution), changed);
        Assert.Equal(2, canExecute);

        changed.Clear();
        model.IsDeny = true;
        Assert.Contains(nameof(ModSecurityRulesModel.IsDeny), changed);
        Assert.Contains(nameof(ModSecurityRulesModel.IsLogOnly), changed);
        Assert.Contains(nameof(ModSecurityRulesModel.IsClose), changed);
        Assert.DoesNotContain(nameof(ModSecurityRulesModel.HasRules), changed);
    }

    [Fact]
    public void TheModelShowsOnlyTextFlagsAndItsCommand()
    {
        Type type = typeof(ModSecurityRulesModel);
        Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property =>
            Assert.True(property.PropertyType == typeof(string) || property.PropertyType == typeof(bool)
                        || property.PropertyType == typeof(IAsyncRelayCommand), property.Name));

        Type[] forbidden = [typeof(ModSecurityRuleRequest), typeof(ModSecurityRuleSet), typeof(ModSecurityLocationNote)];
        Assert.All(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), field =>
        {
            Assert.DoesNotContain(field.FieldType, forbidden);
            Assert.Null(field.FieldType.GetMethod("<Clone>$"));
        });

        Assert.Null(type.GetMethod(nameof(ToString), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            Type.EmptyTypes));
        Assert.Null(typeof(ModSecurityRulesPrompt).GetMethod(nameof(ToString),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, Type.EmptyTypes));
        Assert.DoesNotContain(Folder, Prompt().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePromptChecksItsArgumentsAndCopiesItsLists()
    {
        Assert.Throws<ArgumentNullException>(() => new ModSecurityRulesPrompt(null!, [1], "x", Folder, ModSecurityAction.LogOnly));
        Assert.Throws<ArgumentNullException>(() => new ModSecurityRulesPrompt(["a.txt"], null!, "x", Folder, ModSecurityAction.LogOnly));
        Assert.Throws<ArgumentNullException>(() => new ModSecurityRulesPrompt(["a.txt"], [1], null!, Folder, ModSecurityAction.LogOnly));
        Assert.Throws<ArgumentNullException>(() => new ModSecurityRulesPrompt(["a.txt"], [1], "x", null!, ModSecurityAction.LogOnly));
        Assert.Throws<ArgumentException>(() => new ModSecurityRulesPrompt(["a.txt", "b.txt"], [1], "x", Folder,
            ModSecurityAction.LogOnly));

        string[] files = ["a.txt"];
        int[] ids = [10000];
        var prompt = new ModSecurityRulesPrompt(files, ids, "x", Folder, ModSecurityAction.LogOnly);
        files[0] = "b.txt";
        ids[0] = 20000;

        Assert.Equal(["a.txt"], prompt.FileNames);
        Assert.Equal([10000], prompt.RuleIds);
    }

    [Fact]
    public void NeitherTheModelNorTheDialogNorThePromptsTrace()
    {
        string folder = Path.Combine(RepositoryRoot(), "src", "PoorMansFeed");
        foreach (string name in new[] { "ModSecurityRulesModel.cs", "ModSecurityRulesDialog.axaml.cs", "DialogModels.cs" })
        {
            string text = File.ReadAllText(Path.Combine(folder, name));
            foreach (string call in new[] { "Trace.", "Debug.", "Console." })
            {
                Assert.False(text.Contains(call, StringComparison.Ordinal), name + " holds " + call);
            }
        }
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
