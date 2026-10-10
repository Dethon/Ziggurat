using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Domain.Tools;

// A thing a call can touch, named as a path from the general to the particular: the filesystem,
// a mount in it, a directory on the mount, a file in the directory. Two resources overlap when
// one names the other or something inside it, so a directory overlaps every file under it and
// the empty path — everything — overlaps whatever else is named. Compared without regard to
// case: the registry matches mount points that way, and a conflict found where there was none
// costs a moment while one missed costs the race.
public sealed class SharedResource
{
    private readonly string[] _segments;

    private SharedResource(string[] segments) => _segments = segments;

    public static readonly SharedResource Everything = new([]);

    public static SharedResource Of(params string[] segments) => new([.. segments]);

    public SharedResource Under(params string[] segments) => new([.. _segments, .. segments]);

    public bool Overlaps(SharedResource other) =>
        _segments.Zip(other._segments).All(pair =>
            string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => _segments.Length == 0 ? "(everything)" : string.Join('/', _segments);
}

public sealed record ResourceClaim(SharedResource Resource, bool Changes);

// What one call touches, and whether it only reads it. Two calls a model wrote in one message
// conflict when what they touch overlaps and at least one of them may change it; those take
// effect in the order they were written, and everything else in the message runs at once.
//
// Touching nothing and saying nothing are opposites. `Nothing` is a claim — a call that shares
// no state with any other — and it conflicts with no call at all. A tool that declares no
// footprint is read as `Everything`, which conflicts with every call that touches anything.
public sealed class CallFootprint
{
    private readonly ResourceClaim[] _claims;

    private CallFootprint(ResourceClaim[] claims) => _claims = claims;

    public static readonly CallFootprint Nothing = new([]);

    public static readonly CallFootprint Everything = Changing(SharedResource.Everything);

    public static CallFootprint Reading(params SharedResource[] resources) =>
        new([.. resources.Select(r => new ResourceClaim(r, Changes: false))]);

    public static CallFootprint Changing(params SharedResource[] resources) =>
        new([.. resources.Select(r => new ResourceClaim(r, Changes: true))]);

    public IReadOnlyList<ResourceClaim> Claims => _claims;

    public CallFootprint And(CallFootprint other) => new([.. _claims, .. other._claims]);

    public bool ConflictsWith(CallFootprint other) =>
        _claims.Any(mine => other._claims.Any(theirs =>
            (mine.Changes || theirs.Changes) && mine.Resource.Overlaps(theirs.Resource)));

    public override string ToString() => _claims.Length == 0
        ? "(nothing)"
        : string.Join(", ", _claims.Select(c => $"{(c.Changes ? "changes" : "reads")} {c.Resource}"));
}

// A tool's own word on what a call to it touches, read off that call's arguments. It travels on
// the function, as ExecReach does, so whoever schedules a call asks the function it is about to
// invoke rather than keeping a list of names that a new tool would silently be missing from.
public sealed class FootprintDeclaration(Func<IReadOnlyDictionary<string, object?>, CallFootprint> footprintOf)
{
    public static FootprintDeclaration Always(CallFootprint footprint) => new(_ => footprint);

    // A declaration that cannot read the arguments it was handed has declared nothing about this
    // call, and scheduling must never be what takes a call down.
    public CallFootprint For(IReadOnlyDictionary<string, object?> arguments)
    {
        try
        {
            return footprintOf(arguments);
        }
        catch (Exception)
        {
            return CallFootprint.Everything;
        }
    }

    public static CallFootprint Of(AIFunction tool, IReadOnlyDictionary<string, object?> arguments) =>
        tool.GetService<FootprintDeclaration>() is { } declaration
            ? declaration.For(arguments)
            : CallFootprint.Everything;

    // The tool as the session offers it, answering GetService<FootprintDeclaration>() with this
    // declaration. Everything the model sees is the inner function's.
    public static AIFunction Declared(
        AIFunction tool, Func<IReadOnlyDictionary<string, object?>, CallFootprint> footprintOf) =>
        new DeclaringFunction(tool, new FootprintDeclaration(footprintOf));

    public static AIFunction Declared(AIFunction tool, CallFootprint footprint) =>
        new DeclaringFunction(tool, Always(footprint));

    private sealed class DeclaringFunction(AIFunction inner, FootprintDeclaration declaration)
        : DelegatingAIFunction(inner)
    {
        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(FootprintDeclaration)
                ? declaration
                : base.GetService(serviceType, serviceKey);
    }
}

// An argument as a model wrote it and as a caller in this process handed it over: text either
// way, whichever of the two shapes it arrived in.
public static class CallArguments
{
    public static string? Text(IReadOnlyDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out var value) ? Text(value) : null;

    public static IReadOnlyList<string> Texts(IReadOnlyDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out var value)
            ? value switch
            {
                null or string => [],
                JsonElement { ValueKind: JsonValueKind.Array } array =>
                    [.. array.EnumerateArray().Select(e => Text(e)).OfType<string>()],
                JsonElement => [],
                IEnumerable items => [.. items.Cast<object?>().Select(Text).OfType<string>()],
                _ => []
            }
            : [];

    private static string? Text(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        null => null,
        _ => value.ToString()
    };
}