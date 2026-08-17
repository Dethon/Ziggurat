using Domain.Contracts;

namespace Domain.Tools.FileSystem;

// What file_read needs in order to show the model an image, in one record because every part of it
// is answered per turn rather than per tool set: which conversation and tool call the bytes are
// keyed by, whether the model this turn actually resolved to accepts images, and where the bytes go.
//
// The model check is a delegate rather than a value for the same reason FeatureConfig's conversation
// context is: a tool set is built once per session and a turn can override the model, so a captured
// answer would be the wrong one for exactly the turn that changed it.
//
// A host with no store hands null for Store and the tool answers honestly rather than failing —
// image reading is the part that degrades, and text reading is untouched.
public sealed record ReadImageSupport
{
    public const long DefaultMaxBytes = 15L * 1024 * 1024;

    // The one way to say "this host shows no images", so a caller cannot express it twice.
    public static ReadImageSupport None { get; } = new() { ConversationId = "" };

    public required string ConversationId { get; init; }

    public IReadImageStore? Store { get; init; }

    public Func<string?> CurrentCallId { get; init; } = () => null;

    public Func<bool> ModelAcceptsImages { get; init; } = () => true;

    public long MaxBytes { get; init; } = DefaultMaxBytes;
}