using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Domain.Contracts;

// The channel connections the agent holds now, and every one it comes to hold. A household's own
// channel server's connection appears when that household first opens its tunnel and stays, not
// connected while the tunnel is closed (ADR 0011), so the set only grows. Enumerating it reads the
// ones held now; the monitor and the alert relay watch it, so a connection added after they started
// is read like one that was there.
[PublicAPI]
[CollectionBuilder(typeof(ConnectionSet), nameof(ConnectionSet.Fixed))]
public interface IConnectionSet<out T> : IReadOnlyCollection<T>
{
    // Every connection held now, then each one as it is added. A fixed set ends after the ones it
    // holds — a monitor over it ends when they all do — and the agent's live set never ends.
    IAsyncEnumerable<T> WatchAsync(CancellationToken ct);
}

public static class ConnectionSet
{
    public static IConnectionSet<T> Fixed<T>(ReadOnlySpan<T> connections) => new FixedSet<T>([.. connections]);

    public static IConnectionSet<T> Of<T>(IEnumerable<T> connections) => new FixedSet<T>([.. connections]);

    // The same set seen as one of a connection's other contracts (a channel connection as an alert
    // channel), the connections that are not one left out.
    public static IConnectionSet<TOut> As<TOut>(this IConnectionSet<object> connections) => new ViewedSet<TOut>(connections);

    private sealed class ViewedSet<T>(IConnectionSet<object> connections) : IConnectionSet<T>
    {
        public int Count => connections.OfType<T>().Count();

        public IEnumerator<T> GetEnumerator() => connections.OfType<T>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public IAsyncEnumerable<T> WatchAsync(CancellationToken ct) => connections.WatchAsync(ct).OfType<T>();
    }

    private sealed class FixedSet<T>(IReadOnlyList<T> connections) : IConnectionSet<T>
    {
        public int Count => connections.Count;

        public IEnumerator<T> GetEnumerator() => connections.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public IAsyncEnumerable<T> WatchAsync(CancellationToken ct) => connections.ToAsyncEnumerable();
    }
}