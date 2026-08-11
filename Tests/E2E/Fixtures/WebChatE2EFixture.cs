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

    // The collection's own space, so its conversations are not in the sidebar the collection
    // running beside it is reading. Tests navigate here and nowhere else.
    public string WebChatUrl =>
        _stack is { WebChatUrl: { Length: > 0 } url } ? url + _space : "";

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

    // Walks this collection's own block of user identities, one per test and never back. The
    // modulo is a backstop for a collection that outgrows its block, not the intended path — see
    // WebChatStack for what a reused identity costs.
    private int _userIndex = -1;
    private int _userBlock;
    private string _space = "";

    public int NextUserIndex() =>
        _userBlock + (int)((uint)Interlocked.Increment(ref _userIndex) % WebChatStack.UsersPerCollection);

    protected override Task BuildImagesAsync(CancellationToken ct) => WebChatStack.EnsureImagesAsync(ct);

    protected override async Task StartContainersAsync(CancellationToken ct)
    {
        _stack = await WebChatStack.AcquireAsync(ct);
        (_userBlock, _space) = _stack.ReserveSlice();
    }

    protected override Task StopContainersAsync() => WebChatStack.ReleaseAsync();
}

// The WebChat E2E classes are split across collections so they run at once against the one stack
// rather than as a single serial chain, which was the run's critical path.
//
// The split is by shared state first: the dictation suite is alone because it is the only one that
// writes the whisper stub's transcript, and the chat suites sit together because they are the ones
// whose sidebar rows each other's topics would appear in.
//
// Beyond that it is by length, because a collection is what xUnit serializes and the run is as long
// as its longest chain. The two gesture suites used to share one, which ran them back to back for
// seventy seconds while nothing else was left to overlap them with — they drive the same mobile
// drawer but never the same sidebar, so a collection each costs only the browser it opens. Chat
// keeps its three classes: at forty-odd seconds that chain still finishes inside dictation's, and
// splitting it further buys nothing while opening two more browsers, which is what the machine
// actually runs out of.
[CollectionDefinition(WebChatE2ECollections.Chat)]
public class WebChatE2EChatCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.Dictation)]
public class WebChatE2EDictationCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.Hearth)]
public class WebChatE2EHearthCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.Flick)]
public class WebChatE2EFlickCollection : ICollectionFixture<WebChatE2EFixture>;

public static class WebChatE2ECollections
{
    public const string Chat = "WebChatE2E.Chat";
    public const string Dictation = "WebChatE2E.Dictation";
    public const string Hearth = "WebChatE2E.Hearth";
    public const string Flick = "WebChatE2E.Flick";
}