using System.Text.Json.Nodes;
using Domain.Editing;
using Shouldly;

namespace Tests.Unit.Accounts;

// Two owners with the same admin page open each save what they changed: the page sends what it loaded
// beside what it wants, and each field it changed is laid over the section as it is now, so another
// owner's change to anything else stands. Only a field both changed, to different values, is refused.
public class EditMergeTests
{
    private static readonly IReadOnlyDictionary<string, string> _keys = new Dictionary<string, string> { ["spaces"] = "slug" };

    private static JsonNode Json(string text) => JsonNode.Parse(text)!;

    [Fact]
    public void TwoOwnersAddingASpaceEach_BothSpacesStand()
    {
        var loaded = Json("""{ "spaces": [ { "slug": "default", "name": "Main" } ] }""");
        var mine = Json("""{ "spaces": [ { "slug": "default", "name": "Main" }, { "slug": "kids", "name": "Kids" } ] }""");
        var stored = Json("""{ "spaces": [ { "slug": "default", "name": "Main" }, { "slug": "kitchen", "name": "Kitchen" } ] }""");

        var merged = EditMerge.Merge(loaded, mine, stored, _keys);

        merged.Conflicts.ShouldBeEmpty();
        merged.Value!["spaces"]!.AsArray().Select(s => (string)s!["slug"]!).ShouldBe(["default", "kitchen", "kids"]);
    }

    [Fact]
    public void ASpaceRemovedHere_AndRenamedThere_IsAConflictNamingIt()
    {
        var loaded = Json("""{ "spaces": [ { "slug": "default", "name": "Main" }, { "slug": "attic", "name": "Attic" } ] }""");
        var mine = Json("""{ "spaces": [ { "slug": "default", "name": "Main" } ] }""");
        var stored = Json("""{ "spaces": [ { "slug": "default", "name": "Main" }, { "slug": "attic", "name": "Loft" } ] }""");

        var merged = EditMerge.Merge(loaded, mine, stored, _keys);

        merged.Conflicts.ShouldBe([["spaces", "attic"]]);
    }

    [Fact]
    public void DifferentFieldsOfOneSpace_AreBothKept()
    {
        var loaded = Json("""{ "spaces": [ { "slug": "default", "name": "Main", "accentColor": "#e9601f" } ] }""");
        var mine = Json("""{ "spaces": [ { "slug": "default", "name": "Home", "accentColor": "#e9601f" } ] }""");
        var stored = Json("""{ "spaces": [ { "slug": "default", "name": "Main", "accentColor": "#112233" } ] }""");

        var merged = EditMerge.Merge(loaded, mine, stored, _keys);

        merged.Conflicts.ShouldBeEmpty();
        var space = merged.Value!["spaces"]![0]!;
        ((string)space["name"]!).ShouldBe("Home");
        ((string)space["accentColor"]!).ShouldBe("#112233");
    }

    [Fact]
    public void OneFieldChangedToTwoValues_IsAConflict_AndTheSameValueIsNot()
    {
        var loaded = Json("""{ "timeZone": "Europe/Madrid", "language": null }""");

        EditMerge.Merge(loaded, Json("""{ "timeZone": "Asia/Kolkata", "language": null }"""),
                Json("""{ "timeZone": "Asia/Dubai", "language": null }"""), _keys)
            .Conflicts.ShouldBe([["timeZone"]]);
        EditMerge.Merge(loaded, Json("""{ "timeZone": "Asia/Kolkata", "language": null }"""),
                Json("""{ "timeZone": "Asia/Kolkata", "language": "en" }"""), _keys)
            .Value!.ToJsonString().ShouldBe("""{"timeZone":"Asia/Kolkata","language":"en"}""");
    }

    // What the stored section carries and the page never sent (its version, a field from after the
    // page was built) is left as it is.
    [Fact]
    public void AFieldThePageNeverSent_IsKeptAsStored()
    {
        var merged = EditMerge.Merge(
            Json("""{ "memory": true }"""), Json("""{ "memory": false }"""),
            Json("""{ "schemaVersion": 1, "memory": true, "workers": false }"""), _keys);

        merged.Conflicts.ShouldBeEmpty();
        merged.Value!.ToJsonString().ShouldBe("""{"schemaVersion":1,"memory":false,"workers":false}""");
    }
}