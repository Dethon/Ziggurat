namespace Tests.E2E.Fixtures;

// When the conversation list has stopped moving, read off two snapshots of it.
//
// A snapshot is the rows in order, streaming ones marked with a leading '*'. Two questions are
// asked of it, and they are deliberately asked of different things. The order must agree across
// both readings — over every row, because any row that jumps moves the ones a test is aiming at.
// Streaming is asked only of the rows the test itself made, matched by the per-run tag their names
// carry: a sibling test's reply that never completed leaves its row pulsing for the rest of the
// run, and nothing this test does can finish it. Waiting on that is waiting for the deadline.
internal static class RowSettle
{
    internal static bool HasSettled(string previous, string snapshot, string tag) =>
        snapshot == previous
        && previous.Length > 0
        && !snapshot.Split('|').Any(row => row.StartsWith('*') && row.Contains(tag, StringComparison.Ordinal));
}