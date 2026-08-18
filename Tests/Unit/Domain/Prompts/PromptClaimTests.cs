using System.Reflection;
using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// A claim is one falsifiable statement a section makes about what the agent will do, declared
// beside the prose that teaches it (ADR-0031). These are the rules that keep the declaration a
// declaration rather than a second, drifting copy of the prose.
public class PromptClaimTests
{
    [Fact]
    public void EveryClaim_HasAStableId_AndAOneLineStatement()
    {
        foreach (var claim in PromptManifest.Claims)
        {
            claim.Id.ShouldNotBeNullOrWhiteSpace();
            claim.Statement.ShouldNotBeNullOrWhiteSpace($"'{claim.Id}' states nothing");
            claim.Statement.ShouldNotContain("\n", Case.Sensitive, $"'{claim.Id}'");
        }
    }

    [Fact]
    public void ClaimIds_AreUniqueAcrossEverySection()
    {
        // A scenario cites an id, so two sections claiming the same one would make a citation
        // ambiguous — and the coverage test would report a claim as covered by somebody else's
        // scenario.
        PromptManifest.Claims
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ShouldBeEmpty();
    }

    [Fact]
    public void AClaimId_IsPrefixedWithTheSectionThatDeclaresIt()
    {
        // Readable in a scorecard and in a citation without looking anything up: the prefix says
        // which prose to delete when demonstrating a scenario red.
        foreach (var declaration in PromptManifest.Declarations.Where(d => d.Claims.Count > 0))
        {
            var prefix = declaration.Claims.First().Id.Split('.')[0];
            declaration.Claims.ShouldAllBe(c => c.Id.StartsWith(prefix + "."));
        }
    }

    [Fact]
    public void TheTimerContract_DeclaresItsClaimsInFull()
    {
        // Declared up front, including the ones nothing tests yet, because declaring claims only
        // where scenarios exist leaves the untested rules undeclared forever.
        var timers = PromptManifest.Find(TimerPrompt.Name).ShouldNotBeNull();

        timers.Claims.ShouldContain(c => c.Id == "timers.duration-is-a-countdown");
        timers.Claims.ShouldContain(c => c.Id == "timers.agent-acts-is-a-scheduled-task");
        timers.Claims.ShouldContain(c => c.Id == "timers.clock-time-is-a-calendar-alarm");
        timers.Claims.ShouldContain(c => c.Id == "timers.voice-targets-the-speaking-room");
        timers.Claims.ShouldContain(c => c.Id == "timers.no-satellite-asks-which-room");
        timers.Claims.Count.ShouldBeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void EveryDeclaredClaim_IsListedByTheSectionThatDeclaresIt()
    {
        // A claim declared as a field and left out of its section's list is invisible: the manifest
        // never sees it, the coverage test never asks for it, and the rule it names goes back to
        // being an assumption — which is the one failure ADR-0031 exists to prevent.
        var unlisted = typeof(PromptManifest).Assembly.GetTypes()
            .Select(type => (Type: type, Listed: Listed(type)))
            .Where(section => section.Listed is not null)
            .SelectMany(section => Declared(section.Type)
                .Where(field => !section.Listed!.Any(claim => claim.Id == field.Value.Id))
                .Select(field => $"{section.Type.Name}.{field.Name}"))
            .ToList();

        unlisted.ShouldBeEmpty(
            "these claims are declared as fields and left out of their section's list: " +
            string.Join(", ", unlisted));
    }

    private static IReadOnlyList<PromptClaim>? Listed(Type type) =>
        type.GetField("Claims", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            as IReadOnlyList<PromptClaim>;

    private static IEnumerable<(string Name, PromptClaim Value)> Declared(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(PromptClaim))
            .Select(field => (field.Name, (PromptClaim)field.GetValue(null)!));

    [Fact]
    public void TheManifest_EnumeratesEveryClaimOfEverySection()
    {
        PromptManifest.Claims.Count.ShouldBe(PromptManifest.Declarations.Sum(d => d.Claims.Count));
    }
}