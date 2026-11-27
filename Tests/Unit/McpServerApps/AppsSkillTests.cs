using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Apps;
using Domain.DTOs.FileSystem;
using Domain.Prompts;
using Domain.Tools;
using Domain.Tools.FileSystem;
using McpServerApps.Checks;
using McpServerApps.Publishing;
using McpServerApps.Serving;
using McpServerApps.Settings;
using McpServerApps.Vfs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.Integration.McpServers;
using Tests.Unit.Domain.Prompts;

namespace Tests.Unit.McpServerApps;

// The apps skill teaches a model names it will write into code and calls: an action, a library member, a
// manifest field, a key of a report or of the errors file. A rename fails nothing at build and leaves the
// sentence as convincing as it was, so every such name the skill's words use is held here against the code
// that answers it, and its worked examples are put through the server's own rules. Paths are walked by
// PromptStalenessTests, which reads every skill.
public class AppsSkillTests
{
    private static readonly string _body = AppsSkill.Body;

    // The skill comes with the apps server, which the household's apps feature withholds: an agent that
    // dials it is offered the skill, one that does not never sees it, and the one shipped agent that
    // dials it still fits the ceiling with it advertised.
    [Fact]
    public void TheSkill_IsListedToAnAgentThatDialsTheAppsServer_AndToNoOther()
    {
        var builder = AgentPromptFixture.Assemble("template." + AgentTemplates.Jonas);
        var home = AgentPromptFixture.Assemble("jonas");

        builder.Skills.Select(s => s.Name).ShouldContain(AppsSkill.Name);
        home.Skills.Select(s => s.Name).ShouldNotContain(AppsSkill.Name);
        builder.TokenCount.ShouldBeLessThanOrEqualTo(PromptManifest.MaxAgentPromptTokens);
    }

    [Fact]
    public void EveryActionItRuns_IsAnActionOfTheMount_AndEveryActionIsTaught()
    {
        var named = Regex.Matches(_body, @"`\./([a-z_-]+)").Select(m => m.Groups[1].Value).Distinct();

        named.ShouldBe(AppActions.Actions, ignoreOrder: true);
    }

    // A file tool is named as the model calls it; a wrong leaf passes the staleness walk, which only
    // refuses bare and fs_ names.
    [Fact]
    public void EveryFileToolItCalls_IsOneTheAgentIsOffered()
    {
        var named = Regex.Matches(_body, @"domain__filesystem__(\w+)").Select(m => m.Groups[1].Value).Distinct().ToList();

        named.ShouldNotBeEmpty();
        named.ShouldBeSubsetOf(FileTools());
        named.ShouldAllBe(tool => _body.Contains(FileSystemToolFeature.Callable(tool)));
    }

    // The flags an action takes, as the mount describes its exec.
    [Fact]
    public void EveryFlagItPasses_IsOneTheMountsActionsTake()
    {
        using var provider = ConfiguredServer();
        var exec = provider.GetRequiredService<AppsFileSystem>().DescribeExec;

        Regex.Matches(_body, @"--[a-z]+").Select(m => m.Value).Distinct()
            .Where(flag => !exec.Contains(flag + " ", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    [Fact]
    public void EveryLibraryMemberItCalls_IsExportedByTheLibrary_AndEveryExportIsTaught()
    {
        var exports = Regex.Match(AppOriginFiles.Module, @"export const \{ ([^}]*) \} = apps;").Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries);
        var named = Regex.Matches(_body, @"(?<![/\w])apps\.([A-Za-z]+)").Select(m => m.Groups[1].Value).Distinct();

        exports.ShouldNotBeEmpty();
        named.ShouldBe(exports, ignoreOrder: true);
    }

    [Fact]
    public void EveryServerPathAPageImports_IsOneTheOriginServes()
    {
        var served = new[] { AppOriginFiles.ModulePath, AppOriginFiles.LibraryPath };

        Regex.Matches(_body, @"'(/_app/[^']+)'").Select(m => m.Groups[1].Value)
            .ShouldAllBe(path => served.Contains(path));
    }

    // `apps.query(...)` → `{columns, rows}`: each key is one the tool's own result carries, and each
    // option one the read takes by that name.
    [Theory]
    [InlineData("query", typeof(FsQueryResult))]
    [InlineData("read", typeof(FsReadResult))]
    [InlineData("glob", typeof(FsGlobResult))]
    [InlineData("search", typeof(FsSearchResult))]
    public void EveryResultKeyItShows_IsOneTheToolsResultCarries(string member, Type result)
    {
        var shape = Regex.Match(_body, $@"`apps\.{member}\((?<args>[^`]*)\)` → `\{{(?<keys>[^}}]*)\}}`");

        shape.Success.ShouldBeTrue($"the skill shows apps.{member}'s result as {{keys}}");
        Names(shape.Groups["keys"].Value).ShouldBeSubsetOf(JsonNames(result));
        var options = Regex.Match(shape.Groups["args"].Value, @"\{([^}]*)\}");
        if (options.Success)
        {
            Names(options.Groups[1].Value).ShouldAllBe(option => AppReadSource.Contains($"\"{option}\""));
        }
    }

    [Fact]
    public void TheReadErrorsFieldsAndCodesItNames_AreTheLibrarysAndTheTaxonomys()
    {
        var paragraph = Paragraph("A refused read throws");
        var (fields, codes) = (Before(paragraph, "Show it"), After(paragraph, "Show it"));

        Ticked(fields).Where(t => t != "apps.ReadError")
            .ShouldAllBe(field => field == "message" || AppOriginFiles.Module.Contains($"this.{field} ="));
        Ticked(codes).ShouldNotBeEmpty();
        Ticked(codes).ShouldBeSubsetOf(Constants(typeof(ToolError.Codes)));
    }

    [Fact]
    public void TheExampleManifest_IsOneTheServerKeeps_AndTheFieldsItNamesAreItsFields()
    {
        var json = Fenced("json");

        AppManifest.Parse(json).ShouldBeOfType<AppManifestParse.Valid>();
        JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ShouldBeSubsetOf(AppManifest.Fields);
        Ticked(Paragraph("`reads` are"))
            .Where(t => !t.Contains(' ') && !t.Contains('/'))
            .Where(t => t != "dist")
            .ShouldBeSubsetOf(AppManifest.Fields);
    }

    // The worked example is what ./publish holds a page to, from a CDN the apps server ships, and the
    // skill names exactly the CDNs it ships.
    [Fact]
    public void ThePinnedScriptExample_PassesThePublishCheck_FromAShippedCdn()
    {
        var html = Fenced("html");
        var cdns = ShippedSettings().Cdns;

        OutsideCodePinning.Problems("index.html", html).ShouldBeEmpty();
        cdns.ShouldContain(Regex.Match(html, "src=\"(https://[^/\"]+)").Groups[1].Value);
        Regex.Matches(_body, "`(https://[a-z0-9.-]+)`").Select(m => m.Groups[1].Value)
            .ShouldBe(cdns, ignoreOrder: true);
    }

    [Fact]
    public void WhatACheckLeaves_IsNamedAsTheMountShowsIt()
    {
        _body.ShouldContain($"`/apps/<slug>/{AppsFileSystem.ChecksOutput}/{AppChecks.LatestDirectory}/`");
        _body.ShouldContain($"`{AppActions.AssertionsDirectory}/*.js`");

        Ticked(Paragraph($"- `{AppChecks.ReportFile}`"))
            .Where(t => t != AppChecks.ReportFile)
            .ShouldBeSubsetOf(JsonNames(typeof(ViewportReport)));
    }

    [Fact]
    public void TheErrorsFile_IsNamedAsTheMountShowsIt_AndEveryKeyAndValueIsAnEntrys()
    {
        var path = $"/apps/<slug>/{AppsFileSystem.ErrorsOutput}";
        var words = JsonNames(typeof(AppErrorEntry))
            .Concat(AppErrors.Kinds.All)
            .Append(AppErrorEntry.Published)
            .Append(AppErrorEntry.Preview)
            .Append(path);

        Ticked(Paragraph($"`{path}`")).ShouldBeSubsetOf(words.ToList());
    }

    // Every field app.json takes is taught, by name, so a field added for a feature is a sentence too.
    [Fact]
    public void EveryManifestField_IsTaught()
    {
        AppManifest.Fields.ShouldAllBe(field => _body.Contains($"`{field}`") || _body.Contains($"\"{field}\""));
    }

    // The ask fields it shows as JSON (`"asks": [...]`) are ones the server keeps, beside the worked manifest.
    [Fact]
    public void TheAskFieldsItShows_AreOnesTheManifestKeeps()
    {
        var shown = Regex.Matches(Section("Asking the assistant"), "`(\"(?<field>\\w+)\": [^`]+)`")
            .Select(m => (Field: m.Groups["field"].Value, Json: m.Groups[1].Value)).ToList();
        var manifest = Fenced("json").TrimEnd().TrimEnd('}') + string.Concat(shown.Select(s => ", " + s.Json)) + "}";

        shown.Select(s => s.Field).ShouldBe(["instructions", "asks", "model"], ignoreOrder: true);
        AppManifest.Parse(manifest).ShouldBeOfType<AppManifestParse.Valid>();
    }

    // The servers it names as an ask's are the shared servers' keys, the names a manifest declares them by.
    [Fact]
    public void TheSharedServersItNames_AreTheSettingsKeys()
    {
        var named = Ticked(Paragraph("- `\"asks\":")).Where(t => !t.Contains(' ') && !t.Contains('"')).ToList();

        named.ShouldNotBeEmpty();
        named.ShouldBeSubsetOf(JsonNames(typeof(global::Domain.DTOs.SharedServers)));
    }

    // `apps.ask(text, {...})` → `{conversation, text}`: each option one the request takes by that name or the
    // library takes for itself, every one of them taught, and each answer's keys the answer's.
    [Fact]
    public void TheAskCall_TakesTheRequestsOptions_AndAnswersTheAnswersKeys()
    {
        var shape = Regex.Match(_body,
            @"`apps\.ask\(text, \{(?<options>[^}]*)\}\)` → `\{(?<text>[^}]*)\}`, or `\{(?<value>[^}]*)\}`");
        var libraryOwn = Regex.Match(AppOriginFiles.Module, @"const ask = async \(text, \{ (?<own>[^.}]*), \.\.\.options \}")
            .Groups["own"].Value;
        var requestOptions = JsonNames(typeof(AppAskRequest)).Where(o => o != "text");

        shape.Success.ShouldBeTrue("the skill shows apps.ask's options and both answers");
        Names(shape.Groups["options"].Value).ShouldBe(requestOptions.Concat(Names(libraryOwn)), ignoreOrder: true);
        Names(shape.Groups["text"].Value).Concat(Names(shape.Groups["value"].Value)).Distinct()
            .ShouldBe(JsonNames(typeof(AppAskAnswer)), ignoreOrder: true);
        Ticked(Paragraph("- `schema` is")).ShouldContain($"{{{string.Join(", ", JsonNames(typeof(AppAskSchema)))}}}");
    }

    // What an AskError carries is what its class sets, and the envelope's own fields are the wire's.
    [Fact]
    public void TheAskErrorsFields_AreTheLibrarysAndTheWires()
    {
        var paragraph = Paragraph("A refused ask throws");
        var askError = Regex.Match(AppOriginFiles.Module, @"class AskError extends Error \{(?<body>.*?)\n  \}",
            RegexOptions.Singleline).Groups["body"].Value;

        askError.ShouldNotBeEmpty();
        Ticked(Before(paragraph, "Show it")).Where(t => t != "apps.AskError" && !t.StartsWith("envelope."))
            .ShouldAllBe(field => field == "message" || askError.Contains($"this.{field} ="));
        _body.ShouldContain($"`envelope.{AppAsks.FreesAtField}`");
        _body.ShouldContain($"`{AppAsks.ReplyField}`");
        _body.ShouldContain($"`{AppAsks.FailureField}`");
    }

    // Every code an ask can be answered with is taught, and every code it teaches is the taxonomy's: the
    // refusals AppAsks spells, a malformed ask, and the library's own for a page that lost its session.
    [Fact]
    public void EveryCodeAnAskAnswers_IsTaught_AndEveryCodeTaughtIsTheTaxonomys()
    {
        var section = Section("Asking the assistant");
        var spelled = typeof(AppAsks).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(ToolErrorResult))
            .Select(f => ((ToolErrorResult)f.GetValue(null)!).ErrorCode)
            .Concat([
                AppAsks.Refusal("A refusal.").ErrorCode,
                AppAsks.TimedOut(TimeSpan.FromMinutes(2)).ErrorCode,
                AppAsks.Capped(1, null)["errorCode"]!.GetValue<string>(),
                AppAsks.InvalidReply(null)["errorCode"]!.GetValue<string>(),
                ToolError.Codes.InvalidArgument,
                ToolError.Codes.Authentication
            ])
            .Distinct();

        AppOriginFiles.Module.ShouldContain("errorCode: 'authentication'");
        spelled.ShouldAllBe(code => section.Contains($"`{code}`"));
        Ticked(section).Where(t => Regex.IsMatch(t, "^[a-z]+_[a-z_]+$"))
            .ShouldBeSubsetOf(Constants(typeof(ToolError.Codes)));
    }

    // The limits it states are the shipped ones, which a page designs around (a spinner for the wait,
    // shrinking a photo) before any refusal names them.
    [Fact]
    public void TheAskLimitsItStates_AreTheShippedOnes()
    {
        var asks = ShippedSettings().Asks;
        var section = Section("Asking the assistant");
        var invariant = System.Globalization.CultureInfo.InvariantCulture;

        section.ShouldContain(string.Format(invariant, "{0:N0} bytes", AppAsks.MaxInstructionsBytes));
        section.ShouldContain($"up to {asks.MaxImages} images of {asks.MaxImageBytes / (1024 * 1024)} MB each");
        section.ShouldContain($"{asks.DailyCap} a day");
        section.ShouldContain($"up to {asks.Timeout.TotalMinutes:0} minutes");
        asks.FollowUpWindow.ShouldBe(TimeSpan.FromDays(1));
        section.ShouldContain("within a day of its last turn");
        asks.ImageTypes.Select(t => t["image/".Length..].ToUpperInvariant())
            .ShouldAllBe(type => section.ToUpperInvariant().Contains(type));
    }

    // A failed ask's entry in report.json, as the check writes it, and the deadline its asks answer within.
    [Fact]
    public void AFailedAsksEntry_IsNamedAsTheReportWritesIt()
    {
        _body.ShouldContain("`failedAsks`");
        Section("Checking").ShouldContain($"within the check's {ShippedSettings().Checks.Deadline.TotalMinutes:0} minutes");
        Ticked(Paragraph("- A failed ask's entry"))
            .ShouldBeSubsetOf(JsonNames(typeof(FailedAsk)));
    }

    private static readonly string AppReadSource = File.ReadAllText(
        Path.Combine(TestHelpers.FindSolutionRoot(), "Domain", "Tools", "FileSystem", "Bridge", "AppRead.cs"));

    private static IEnumerable<string> Names(string list) =>
        list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<string> Ticked(string text) =>
        [.. Regex.Matches(text, "`([^`\n]+)`").Select(m => m.Groups[1].Value).Distinct()];

    // The line of the body that starts with these words: a paragraph or a bullet is one line.
    private static string Paragraph(string start) =>
        _body.Split('\n').Single(line => line.TrimStart().StartsWith(start, StringComparison.Ordinal));

    // A `### <heading>` section of the body, up to the next heading.
    private static string Section(string heading)
    {
        var start = _body.IndexOf($"### {heading}\n", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"the skill has a '{heading}' section");
        var end = _body.IndexOf("\n### ", start + 4, StringComparison.Ordinal);
        return end < 0 ? _body[start..] : _body[start..end];
    }

    private static string Before(string text, string marker) => text[..text.IndexOf(marker, StringComparison.Ordinal)];

    private static string After(string text, string marker) => text[text.IndexOf(marker, StringComparison.Ordinal)..];

    private static string Fenced(string language)
    {
        var block = Regex.Match(_body, $@"```{language}\n(?<code>.*?)```", RegexOptions.Singleline);
        block.Success.ShouldBeTrue($"the skill has a {language} example");
        return block.Groups["code"].Value;
    }

    private static IReadOnlyList<string> JsonNames(Type type) =>
    [
        .. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
    ];

    private static IReadOnlyList<string> Constants(Type type) =>
    [
        .. type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
    ];

    private static AppsSettings ShippedSettings() =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(TestHelpers.FindSolutionRoot(), "McpServerApps", "appsettings.json"), optional: false)
            .Build()
            .Get<AppsSettings>()!;

    private static ServiceProvider ConfiguredServer()
    {
        var services = new ServiceCollection();
        McpServerRegistrations.Get("apps").Configure(services);
        return services.BuildServiceProvider();
    }

    // Every file tool the agent is offered, by the leaf its own class declares.
    private static IReadOnlyList<string> FileTools() =>
    [
        .. typeof(VfsExecTool).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(VfsExecTool).Namespace && t.Name.StartsWith("Vfs") && t.Name.EndsWith("Tool"))
            .Select(t => t.GetField("Name", BindingFlags.Public | BindingFlags.Static))
            .OfType<FieldInfo>()
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
    ];
}