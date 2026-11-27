using Microsoft.Extensions.AI;

namespace Domain.Tools;

// Whether a call leaves something a later turn acts on as the person — a schedule, a watch, a login
// handoff: what the agent signs a delegation for as the call leaves (DelegatedWrites,
// SigningClientTransport). It travels on the function, as ExecReach does, so the approval client asks
// the very call it is deciding. A built app's ask may leave none: its text, which may carry fetched web
// text, would later run as a full turn of the viewer, outside every bound the ask has.
public sealed class StartsLaterTurns(Func<IReadOnlyDictionary<string, object?>, bool> startedBy)
{
    public static readonly StartsLaterTurns Always = new(_ => true);

    // A declaration that cannot read the arguments it was handed cannot say the call leaves nothing, and
    // only a turn that may leave none asks.
    public bool By(IReadOnlyDictionary<string, object?> arguments)
    {
        try
        {
            return startedBy(arguments);
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static bool Of(AIFunction tool, IReadOnlyDictionary<string, object?> arguments) =>
        tool.GetService<StartsLaterTurns>() is { } declaration && declaration.By(arguments);

    // The tool as the session offers it, answering GetService<StartsLaterTurns>() with this declaration.
    // Everything the model sees is the inner function's.
    public static AIFunction Carried(AIFunction tool, StartsLaterTurns declaration) => new CarryingFunction(tool, declaration);

    private sealed class CarryingFunction(AIFunction inner, StartsLaterTurns declaration) : DelegatingAIFunction(inner)
    {
        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(StartsLaterTurns)
                ? declaration
                : base.GetService(serviceType, serviceKey);
    }
}