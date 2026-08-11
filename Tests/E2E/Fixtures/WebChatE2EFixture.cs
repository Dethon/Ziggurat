namespace Tests.E2E.Fixtures;

// One collection's view of the shared WebChat stack. The containers live in WebChatStack and are
// refcounted across every collection below; what a fixture instance owns for itself is its browser,
// which the base class launches — so each collection keeps its own contexts and CreatePageAsync
// never closes a page a concurrently-running collection is still driving.
public class WebChatE2EFixture : E2EFixtureBase
{
    private WebChatStack? _stack;

    private WebChatStack Stack =>
        _stack ?? throw new InvalidOperationException("Stack not acquired. Call InitializeAsync first.");

    public string WebChatUrl => _stack?.WebChatUrl ?? "";

    public string Transcript
    {
        get => Stack.Transcript;
        set => Stack.Transcript = value;
    }

    public int TranscriptionStatus
    {
        get => Stack.TranscriptionStatus;
        set => Stack.TranscriptionStatus = value;
    }

    public byte[]? LastAudio => Stack.LastAudio;

    public TimeSpan RecordingCap => Stack.RecordingCap;

    // Walks this collection's own block of user identities. Kept local to the fixture — and so to
    // the collection — because a collection's tests run one at a time, which is what makes reusing
    // an identity within the block safe and sharing one across blocks not.
    private int _userIndex = -1;
    private int _userBlock;

    public int NextUserIndex() =>
        _userBlock + (int)((uint)Interlocked.Increment(ref _userIndex) % WebChatStack.UsersPerCollection);

    protected override Task BuildImagesAsync(CancellationToken ct) => WebChatStack.EnsureImagesAsync(ct);

    protected override async Task StartContainersAsync(CancellationToken ct)
    {
        _stack = await WebChatStack.AcquireAsync(ct);
        _userBlock = _stack.ReserveUserBlock();
    }

    protected override Task StopContainersAsync() => WebChatStack.ReleaseAsync();
}

// The WebChat E2E classes are split across collections so they run at once against the one stack
// rather than as a single serial chain, which was the run's critical path. The split is by shared
// state, not by size: the dictation suite is alone because it is the only one that writes the
// whisper stub's transcript, and the two gesture suites sit together because they drive the same
// mobile drawer.
[CollectionDefinition(WebChatE2ECollections.Chat)]
public class WebChatE2EChatCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.Dictation)]
public class WebChatE2EDictationCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.Hearth)]
public class WebChatE2EHearthCollection : ICollectionFixture<WebChatE2EFixture>;

public static class WebChatE2ECollections
{
    public const string Chat = "WebChatE2E.Chat";
    public const string Dictation = "WebChatE2E.Dictation";
    public const string Hearth = "WebChatE2E.Hearth";
}