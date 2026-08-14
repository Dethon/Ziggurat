using System.Diagnostics;

namespace Tests.E2E.Fixtures;

// Where a run spent its wall clock, when the question comes up again.
//
// A .trx says which test was slow; it cannot say which wait inside it was, and the waits that
// decide this suite's length are the ones nothing reports — a fixture phase, a container coming up,
// a settle that quietly reached its cap and passed anyway. Set E2E_TRACE_FILE and each of those
// appends its seconds; leave it unset and every call here is a null check.
internal static class E2ETrace
{
    private static readonly string? _path = Environment.GetEnvironmentVariable("E2E_TRACE_FILE");
    private static readonly Lock _gate = new();
    private static readonly Stopwatch _since = Stopwatch.StartNew();

    internal static void Write(string what, double seconds, string detail = "")
    {
        if (_path is null)
        {
            return;
        }

        // Appended rather than held, so a run killed mid-way still leaves what it had measured.
        lock (_gate)
        {
            File.AppendAllText(_path, $"{_since.Elapsed.TotalSeconds:0.0}\t{what}\t{seconds:0.00}\t{detail}\n");
        }
    }

    internal static async Task<T> TimeAsync<T>(string what, Func<Task<T>> body)
    {
        if (_path is null)
        {
            return await body();
        }

        var elapsed = Stopwatch.StartNew();
        var result = await body();
        Write(what, elapsed.Elapsed.TotalSeconds);
        return result;
    }

    internal static async Task TimeAsync(string what, Func<Task> body)
    {
        if (_path is null)
        {
            await body();
            return;
        }

        var elapsed = Stopwatch.StartNew();
        await body();
        Write(what, elapsed.Elapsed.TotalSeconds);
    }
}