using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.Memory;
using Domain.Tools.SubAgents;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools;

// The rule two calls of one message are ordered by: they conflict when what they touch overlaps
// and at least one of them may change it.
public class CallFootprintTests
{
    private static readonly SharedResource _notes = SharedResource.Of("filesystem", "/vault", "notes");

    [Fact]
    public void AResource_OverlapsItselfWhatItContainsAndWhatContainsIt_AndNothingBesideIt()
    {
        _notes.Overlaps(_notes).ShouldBeTrue();
        _notes.Overlaps(_notes.Under("a.md")).ShouldBeTrue();
        _notes.Under("a.md").Overlaps(_notes).ShouldBeTrue();
        _notes.Overlaps(SharedResource.Everything).ShouldBeTrue();
        _notes.Overlaps(SharedResource.Of("filesystem", "/vault", "journal")).ShouldBeFalse();
        // A name that merely starts the same is another thing: /vault/notes is not /vault/notes-old.
        _notes.Overlaps(SharedResource.Of("filesystem", "/vault", "notes-old")).ShouldBeFalse();
    }

    [Fact]
    public void TwoReads_NeverConflict() =>
        CallFootprint.Reading(_notes).ConflictsWith(CallFootprint.Reading(_notes)).ShouldBeFalse();

    [Fact]
    public void AChange_ConflictsWithAReadOfWhatItChanges_WhicheverIsAsked()
    {
        var change = CallFootprint.Changing(_notes.Under("a.md"));
        var read = CallFootprint.Reading(_notes);

        change.ConflictsWith(read).ShouldBeTrue();
        read.ConflictsWith(change).ShouldBeTrue();
    }

    // Saying nothing and touching nothing are opposites, and must stay that way: one is ordered
    // against every call that touches anything, the other against none.
    [Fact]
    public void ACallThatTouchesNothing_ConflictsWithNoCall_NotEvenOneThatMayTouchEverything()
    {
        CallFootprint.Nothing.ConflictsWith(CallFootprint.Everything).ShouldBeFalse();
        CallFootprint.Everything.ConflictsWith(CallFootprint.Nothing).ShouldBeFalse();
        CallFootprint.Everything.ConflictsWith(CallFootprint.Reading(_notes)).ShouldBeTrue();
        CallFootprint.Everything.ConflictsWith(CallFootprint.Everything).ShouldBeTrue();
    }

    [Fact]
    public void AToolThatDeclaresNothing_IsReadAsTouchingEverything()
    {
        var undeclared = AIFunctionFactory.Create(() => "x", "undeclared");

        FootprintDeclaration.Of(undeclared, new Dictionary<string, object?>())
            .ConflictsWith(CallFootprint.Reading(_notes)).ShouldBeTrue();
    }

    [Fact]
    public void ADeclarationThatCannotReadItsArguments_HasDeclaredNothingAboutThatCall()
    {
        var tool = FootprintDeclaration.Declared(
            AIFunctionFactory.Create(() => "x", "throwing"),
            _ => throw new InvalidOperationException("unreadable"));

        FootprintDeclaration.Of(tool, new Dictionary<string, object?>())
            .ConflictsWith(CallFootprint.Reading(_notes)).ShouldBeTrue();
    }

    // A declared tool is still the tool: the model sees the inner function's name, description
    // and schema, and a service the inner function answers is still answered.
    [Fact]
    public void ADeclaredTool_IsOtherwiseTheToolItWraps()
    {
        var inner = AIFunctionFactory.Create((string path) => path, "inner", "Reads a path.");

        var declared = FootprintDeclaration.Declared(inner, CallFootprint.Nothing);

        declared.Name.ShouldBe("inner");
        declared.Description.ShouldBe("Reads a path.");
        declared.JsonSchema.GetRawText().ShouldBe(inner.JsonSchema.GetRawText());
    }

    // ---- the wire declaration ------------------------------------------------------------------

    [Fact]
    public void AWireFootprint_FillsItsPlaceholdersFromTheCall_OneSegmentEach()
    {
        var page = Parsed("""{"changes":["browser/page/{url}"]}""");

        var first = page.For(Call(("url", "https://example.com/a/b")));

        first.ConflictsWith(page.For(Call(("url", "https://example.com/a/b")))).ShouldBeTrue();
        // The url is one segment however many slashes it holds: a/b is not inside a.
        first.ConflictsWith(page.For(Call(("url", "https://example.com/a")))).ShouldBeFalse();
        first.ConflictsWith(Parsed("""{"changes":["browser"]}""").For(Call())).ShouldBeTrue();
    }

    [Fact]
    public void AWireFootprint_ReadsAnArgumentWrittenAsJson()
    {
        using var json = JsonDocument.Parse("\"https://example.com/a\"");
        var page = Parsed("""{"changes":["browser/page/{url}"]}""");

        page.For(Call(("url", json.RootElement.Clone())))
            .ConflictsWith(page.For(Call(("url", "https://example.com/b")))).ShouldBeFalse();
    }

    // The claim widens to what was named, never narrows.
    [Fact]
    public void APlaceholderTheCallLeftOut_EndsThePathThere()
    {
        var page = Parsed("""{"changes":["browser/page/{url}"]}""");

        page.For(Call()).ConflictsWith(page.For(Call(("url", "https://example.com/a")))).ShouldBeTrue();
    }

    [Fact]
    public void AWireFootprint_SeparatesWhatACallReadsFromWhatItChanges()
    {
        var reader = Parsed("""{"reads":["catalog"]}""");
        var writer = Parsed("""{"changes":["catalog/{id}"]}""");

        reader.For(Call()).ConflictsWith(reader.For(Call())).ShouldBeFalse();
        reader.For(Call()).ConflictsWith(writer.For(Call(("id", "7")))).ShouldBeTrue();
    }

    [Fact]
    public void AnEmptyWireFootprint_IsAToolThatSharesNothing() =>
        Parsed("{}").For(Call()).ConflictsWith(CallFootprint.Everything).ShouldBeFalse();

    // A shape this cannot read is no declaration, so the tool is ordered against everything —
    // never read as a tool that shares nothing.
    [Theory]
    [InlineData("null")]
    [InlineData("\"browser\"")]
    [InlineData("""["browser"]""")]
    [InlineData("""{"changes":"browser"}""")]
    [InlineData("""{"changes":[1]}""")]
    [InlineData("""{"writes":["browser"]}""")]
    public void AWireFootprintThatCannotBeRead_IsNoDeclarationAtAll(string json) =>
        FootprintTemplate.Parse(JsonNode.Parse(json)).ShouldBeNull();

    // ---- the tools that declare a footprint of their own ---------------------------------------

    [Fact]
    public void ForgettingTwoMemoriesById_DoesNotConflict_AndForgettingOneTwiceDoes()
    {
        var forget = new MemoryToolFeature(Mock.Of<IMemoryStore>(), Mock.Of<IEmbeddingService>())
            .GetTools(new FeatureConfig()).ShouldHaveSingleItem();

        var first = FootprintDeclaration.Of(forget, Call(("memoryId", "m-1")));
        using var ids = JsonDocument.Parse("""["m-1","m-3"]""");

        first.ConflictsWith(FootprintDeclaration.Of(forget, Call(("memoryId", "m-2")))).ShouldBeFalse();
        first.ConflictsWith(FootprintDeclaration.Of(forget, Call(("memoryIds", ids.RootElement.Clone())))).ShouldBeTrue();
        first.ConflictsWith(FootprintDeclaration.Of(forget, Call(("memoryIds", new[] { "m-2", "m-3" })))).ShouldBeFalse();
        // By description it can reach any of them.
        first.ConflictsWith(FootprintDeclaration.Of(forget, Call(("query", "my job")))).ShouldBeTrue();
        first.ConflictsWith(CallFootprint.Changing(SharedResource.Of("filesystem", "/vault"))).ShouldBeFalse();
    }

    // Workers handed out in one message are parts the model called independent, and running them
    // side by side is what they are for; a worker written after a change still waits for it.
    [Fact]
    public void TwoWorkers_RunSideBySide_AndAWorkerIsOrderedAgainstAnyChangeBesideIt()
    {
        var run = new SubAgentToolFeature(new SubAgentRegistryOptions { SubAgents = [] })
            .GetTools(new FeatureConfig()).ShouldHaveSingleItem();

        var worker = FootprintDeclaration.Of(run, Call(("subAgentId", "researcher"), ("prompt", "look")));

        worker.ConflictsWith(worker).ShouldBeFalse();
        worker.ConflictsWith(CallFootprint.Reading(_notes)).ShouldBeFalse();
        worker.ConflictsWith(CallFootprint.Changing(_notes)).ShouldBeTrue();
        worker.ConflictsWith(CallFootprint.Everything).ShouldBeTrue();
    }

    private static FootprintDeclaration Parsed(string json) =>
        FootprintTemplate.Parse(JsonNode.Parse(json)).ShouldNotBeNull();

    private static Dictionary<string, object?> Call(params (string Name, object? Value)[] arguments) =>
        arguments.ToDictionary(a => a.Name, a => a.Value);
}