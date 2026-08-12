using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Tests.Unit;

// Collects formatted log messages so a test can assert on what an operator would see.
// The three call sites differ only in which levels they keep, hence the filter.
//
// The queue is concurrent because most of what this captures is written from somewhere the test is
// not: a hosted service's loop, a connection's run, a metrics drain. A plain List being appended
// from there while an assertion enumerates it here throws out of the assertion — "collection was
// modified" attributed to whatever the test happened to be checking — and two appends racing can
// lose one outright. Enumerating a ConcurrentQueue takes its own snapshot, so callers keep the live
// reference they already hold and read a consistent view of it.
internal sealed class CapturingLoggerProvider(Func<LogLevel, bool> keep) : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public CapturingLoggerProvider(LogLevel minimum) : this(level => level >= minimum)
    {
    }

    public IReadOnlyCollection<string> Messages => _messages;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages, keep);

    public void Dispose()
    {
    }

    public static CapturingLoggerProvider ForLevel(LogLevel level) => new(l => l == level);

    private sealed class CapturingLogger(ConcurrentQueue<string> messages, Func<LogLevel, bool> keep)
        : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => keep(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (keep(logLevel))
            {
                messages.Enqueue(formatter(state, exception));
            }
        }
    }
}