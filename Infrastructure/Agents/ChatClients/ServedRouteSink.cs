namespace Infrastructure.Agents.ChatClients;

// Where the last route seen on the wire is kept. Last write wins, because the route arrives on
// whichever chunk carried it and every later chunk of the same response says the same thing.
public sealed class ServedRouteSink
{
    private ServedRoute? _current;

    public ServedRoute? Current => Volatile.Read(ref _current);

    // Merged rather than replaced: the two wires name the model, the provider and the generation
    // on different chunks, and a later chunk carrying only the model must not erase a provider an
    // earlier one named.
    public void Record(ServedRoute route)
    {
        var previous = Current;
        Volatile.Write(ref _current, previous is null
            ? route
            : new ServedRoute(
                route.Model ?? previous.Model,
                route.Provider ?? previous.Provider,
                route.GenerationId ?? previous.GenerationId));
    }
}