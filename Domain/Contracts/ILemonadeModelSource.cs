using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.Contracts;

// The Lemonade chat host's chat models, discovered rather than declared. Reading it never touches
// the network: the answer is whatever the last refresh found, and a host that could not be asked
// left nothing. With no host configured it is the empty source.
[PublicAPI]
public interface ILemonadeModelSource
{
    IReadOnlyList<LemonadeModel> Current { get; }
}

[PublicAPI]
public sealed class FixedLemonadeModelSource(IReadOnlyList<LemonadeModel> models) : ILemonadeModelSource
{
    public static FixedLemonadeModelSource None { get; } = new([]);

    public IReadOnlyList<LemonadeModel> Current => models;
}