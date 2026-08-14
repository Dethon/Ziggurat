using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.E2E;

// The sidebar settles when the rows a test made have stopped moving. What used to be asked
// instead — that no row anywhere in the space is streaming — is a claim about conversations the
// test did not create and cannot finish: a sibling's reply that never completes leaves its row
// pulsing for the rest of the run, and every later wait in that space then spends its whole cap
// before proceeding to pass anyway. That was the difference between a seventy-second suite and a
// two-minute one, so the rule the predicate applies is worth pinning here rather than only in a
// browser.
public class RowSettleTests
{
    private const string Tag = "ab12";

    [Fact]
    public void Rows_ThatAreStillReordering_HaveNotSettled()
    {
        RowSettle.HasSettled($"one {Tag}|two {Tag}", $"two {Tag}|one {Tag}", Tag).ShouldBeFalse();
    }

    [Fact]
    public void TheSameOrderTwice_WithNothingStreaming_HasSettled()
    {
        RowSettle.HasSettled($"one {Tag}|two {Tag}", $"one {Tag}|two {Tag}", Tag).ShouldBeTrue();
    }

    [Fact]
    public void ARowOfThisTestsOwn_StillStreaming_HasNotSettled()
    {
        RowSettle.HasSettled($"*one {Tag}|two {Tag}", $"*one {Tag}|two {Tag}", Tag).ShouldBeFalse();
    }

    // The case the cap was being spent on: a row left streaming by an earlier test in the same
    // collection. It is not this test's to finish, it is not moving, and waiting on it only ever
    // ends at the deadline.
    [Fact]
    public void ARowFromAnotherTest_StillStreaming_DoesNotHoldThisOne()
    {
        RowSettle.HasSettled($"*Renamed 7f66|one {Tag}", $"*Renamed 7f66|one {Tag}", Tag).ShouldBeTrue();
    }

    // A foreign row that is streaming is also a row that can jump: the order check is over the
    // whole list for exactly that reason, and only the streaming question is narrowed.
    [Fact]
    public void AForeignRow_ThatMoves_StillHoldsTheWait()
    {
        RowSettle.HasSettled($"*Renamed 7f66|one {Tag}", $"one {Tag}|*Renamed 7f66", Tag).ShouldBeFalse();
    }

    // An empty first reading is the loop's own starting value, not a list that agrees with itself.
    [Fact]
    public void TheFirstReading_HasNothingToAgreeWith()
    {
        RowSettle.HasSettled("", $"one {Tag}", Tag).ShouldBeFalse();
    }

    // The names are truncated in the DOM, so a tag placed late in a long name is not in the text
    // the predicate is given. Matching is over what a row actually reads.
    [Fact]
    public void ARowWhoseNameCarriesTheTag_IsThisTestsWhereverTheTagSits()
    {
        RowSettle.HasSettled($"*Paged one {Tag} for E2E", $"*Paged one {Tag} for E2E", Tag).ShouldBeFalse();
    }
}