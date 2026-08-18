namespace Tests.Eval.Harness;

// The instant every scenario is pinned to. One value rather than one per family, because the whole
// point of pinning it is that an expected fire time is a string somebody can read in a dump — and
// two families disagreeing about what "this evening" means would make two dumps unreadable side by
// side.
public static class EvalInstant
{
    // A Monday evening in Madrid.
    public static readonly DateTimeOffset Evening =
        new(2026, 8, 17, 20, 0, 0, TimeSpan.FromHours(2));
}