using System.Diagnostics.CodeAnalysis;

namespace Domain.Security.Sealing;

// Why a blob did not open. The two name different mistakes: something that was never a sealed value
// (or one of a format this code does not write), and a sealed value under another key or for another
// place — the tag cannot tell a wrong key from a wrong place or a flipped byte, so neither can this.
public enum SealRefusal
{
    NotSealed,
    DoesNotOpen
}

// Either the value a blob opened to, or why it did not. Never both, never neither: a blob that failed
// carries nothing for anything to fall back on.
public sealed record Unsealing
{
    private Unsealing(byte[]? value, SealRefusal? refusal)
    {
        Value = value;
        Refusal = refusal;
    }

    public byte[]? Value { get; }

    public SealRefusal? Refusal { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Refusal))]
    public bool IsOpened => Value is not null;

    public static Unsealing Opened(byte[] value) => new(value, null);

    public static Unsealing Refused(SealRefusal reason) => new(null, reason);
}