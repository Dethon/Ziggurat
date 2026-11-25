using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Apps;
using Domain.DTOs.FileSystem;
using Domain.Prompts;
using Domain.Tools;
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

        named.ShouldBe(AppsFileSystem.Actions, ignoreOrder: true);
    }

    // The flags an action takes, as the mount describes its exec.
    [Fact]
    public void EveryFlagItPasses_IsOneTheMountsActionsTake()
    {
        var exec = Mount().DescribeExec;

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
        _body.ShouldContain($"`{AppsFileSystem.AssertionsDirectory}/*.js`");

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

    private static readonly string AppReadSource = File.ReadAllText(
        Path.Combine(TestHelpers.FindSolutionRoot(), "Domain", "Tools", "FileSystem", "Bridge", "AppRead.cs"));

    private static IEnumerable<string> Names(string list) =>
        list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<string> Ticked(string text) =>
        [.. Regex.Matches(text, "`([^`\n]+)`").Select(m => m.Groups[1].Value).Distinct()];

    // The line of the body that starts with these words: a paragraph or a bullet is one line.
    private static string Paragraph(string start) =>
        _body.Split('\n').Single(line => line.TrimStart().StartsWith(start, StringComparison.Ordinal));

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

    private static AppsFileSystem Mount()
    {
        var services = new ServiceCollection();
        McpServerRegistrations.Get("apps").Configure(services);
        return services.BuildServiceProvider().GetRequiredService<AppsFileSystem>();
    }
}