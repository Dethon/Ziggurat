namespace Tests.Eval.Fixtures;

// How many runs may hold a stack at once, for the whole test process.
//
// Two independent things put runs in flight — xUnit parallelizes the family classes, and a
// scenario's k of N runs go out together — and neither knows what the other is doing. Multiplied
// out that is thirty-odd stacks, each a sandbox container and seven servers, on one machine and
// against one provider account. The gate is the single number that bounds them, so raising either
// kind of parallelism cannot overcommit the machine.
public static class EvalConcurrency
{
    public const string Variable = "ZIGGURAT_EVAL_PARALLELISM";

    public static RunGate Gate { get; } = new(WidthFrom(
        Environment.GetEnvironmentVariable(Variable), Environment.ProcessorCount));

    // Half the cores by default: a run spends most of its wall clock waiting on the provider, but
    // its stack is real work. The ceiling is about the account rather than the machine — a
    // hundred-core host asking a provider for a hundred concurrent turns gets rate-limited, and a
    // 429 in this suite reads as a behavioural failure it is not.
    public static int WidthFrom(string? configured, int processors) =>
        int.TryParse(configured, out var asked) && asked > 0
            ? asked
            : Math.Clamp(processors / 2, 4, 12);
}

// A slot, held for as long as the run holds its stack. Nothing here knows what a run is: the
// callers that matter are EvalRun, which takes a slot around a stack, and this suite's own tests.
public sealed class RunGate(int width)
{
    private readonly SemaphoreSlim _slots = new(width, width);

    public int Width => width;

    public async Task<T> RunAsync<T>(Func<Task<T>> run)
    {
        await _slots.WaitAsync();
        try
        {
            return await run();
        }
        finally
        {
            _slots.Release();
        }
    }
}