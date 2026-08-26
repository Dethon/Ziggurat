using System.Reflection;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.E2E;

// Every WebChat E2E collection takes a slice: a space of its own and a block of user identities of
// its own. The slice is what lets the collections run at once — a space is the boundary the
// application draws, so two collections sharing one would tap each other's rows, and the dictation
// suites would answer each other's words because the whisper stub keeps its transcript per space.
//
// ReserveSlice hands them out modulo CollectionSlices, so a ninth collection added to an
// eight-slice stack does not fail: it silently wraps onto alpha and shares everything that space
// holds with the collection that got there first. That is a flake with no error message and no
// obvious author, which is why the count is asserted here rather than left to whoever splits the
// next long chain.
public class WebChatCollectionSliceTests
{
    private static IReadOnlyList<string> DeclaredCollections =>
        [.. typeof(WebChatE2ECollections)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)];

    [Fact]
    public void EveryCollection_HasASliceOfItsOwn()
    {
        DeclaredCollections.Count.ShouldBeLessThanOrEqualTo(
            WebChatStack.CollectionSlices,
            $"{DeclaredCollections.Count} WebChat collections share {WebChatStack.CollectionSlices} "
            + "slices — the surplus wrap onto slices already taken. Add a space name and raise "
            + "CollectionSlices to match.");
    }

    // The slice index reaches into _spaces directly, so a stack promising more slices than it has
    // space names throws IndexOutOfRange on whichever collection draws the missing one — during
    // fixture start-up, where it reads as the stack failing to come up.
    [Fact]
    public void EverySliceTheStackPromises_HasASpaceToName()
    {
        var spaces = (string[])typeof(WebChatStack)
            .GetField("_spaces", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        spaces.Length.ShouldBeGreaterThanOrEqualTo(WebChatStack.CollectionSlices);
        spaces.Distinct().Count().ShouldBe(spaces.Length, "two slices would share one space");
    }
}