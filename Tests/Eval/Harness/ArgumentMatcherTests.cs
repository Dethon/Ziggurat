using System.Text.Json;
using Shouldly;

namespace Tests.Eval.Harness;

// The matchers, proven on fixed argument documents. They carry nearly all of the suite's signal,
// and one that is quietly too narrow turns a contract the model honoured into a failure — which is
// worse than no check at all, because it gets the check deleted.
public class ArgumentMatcherTests
{
    [Fact]
    public void ATargetWrittenEitherWay_SatisfiesTheSameExpectation()
    {
        // The contract names two spellings of one target — the room, or the exact id of a
        // satellite in it — so a scenario asking "did it ring where the request came from" has to
        // accept both. Insisting on one makes the model's choice between them the subject.
        var byRoom = Arg.Body("content", Arg.Body("target",
            Arg.Any(Arg.Is("room", "kitchen"), Arg.Is("satelliteId", "kitchen-01"))));

        byRoom.Matches(Args("""{"content":"{\"target\":{\"room\":\"kitchen\"}}"}""")).ShouldBeTrue();
        byRoom.Matches(Args("""{"content":"{\"target\":{\"satelliteId\":\"kitchen-01\"}}"}""")).ShouldBeTrue();
    }

    [Fact]
    public void ATargetThatIsNeitherSpelling_FailsTheExpectation()
    {
        var byRoom = Arg.Any(Arg.Is("room", "kitchen"), Arg.Is("satelliteId", "kitchen-01"));

        byRoom.Matches(Args("""{"room":"office"}""")).ShouldBeFalse();
    }

    [Fact]
    public void TheDescription_NamesEveryAlternative()
    {
        // What a failure prints: a scenario that accepted three spellings and reported one would
        // send whoever reads the dump looking for a rule that does not exist.
        Arg.Any(Arg.Is("room", "kitchen"), Arg.Is("satelliteId", "kitchen-01"))
            .Description.ShouldBe("room = 'kitchen' or satelliteId = 'kitchen-01'");
    }

    [Fact]
    public void ASearchScopedByItsDirectory_HasThatDirectoryAsItsPath()
    {
        // Search spells its scope `directoryPath`, and it passes `filePath: null` alongside — so a
        // path permission that did not know the name matched nothing, and every search in the
        // suite read as a call to an unpermitted place.
        Arg.PathOf(Args("""{"query":"pesto","filePath":null,"directoryPath":"/vault"}"""))
            .ShouldBe("/vault");
    }

    [Fact]
    public void APathSpelledWithATrailingSlash_SatisfiesTheSameExpectation()
    {
        // Glob returns directories with a trailing slash, and a model that removes exactly what
        // the glob handed it is honouring the contract — the slash is the tool's spelling, not a
        // different place.
        Arg.Path("/timers/pasta").Matches(Args("""{"path":"/timers/pasta/"}""")).ShouldBeTrue();
        Arg.Path("/timers/pasta/").Matches(Args("""{"path":"/timers/pasta"}""")).ShouldBeTrue();
        Arg.Path("/timers/pasta").Matches(Args("""{"path":"/timers/pastas"}""")).ShouldBeFalse();
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AFlagMatcher_ReadsABooleanAndNotItsSpelling()
    {
        var call = Args("""{"url":"http://site/x","snapshot":true}""");

        Arg.Flag("snapshot", true).Matches(call).ShouldBeTrue();
        Arg.Flag("snapshot", false).Matches(call).ShouldBeFalse();
        // The string matchers see a boolean as absent, which is why this one exists.
        Arg.Is("snapshot", "true").Matches(call).ShouldBeFalse();
    }
}