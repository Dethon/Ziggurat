namespace Tests.E2E.Fixtures;

// One collection's view of the shared WebChat stack. The containers live in WebChatStack and are
// refcounted across every collection below, as is the one browser the base class launches. What a
// fixture instance owns for itself is the contexts it made, its slice of user identities and its
// space — so CreatePageAsync never closes a page a concurrently-running collection is still driving.
public class WebChatE2EFixture : E2EFixtureBase
{
    private WebChatStack? _stack;

    private WebChatStack Stack =>
        _stack ?? throw new InvalidOperationException("Stack not acquired. Call InitializeAsync first.");

    // The collection's own space, so its conversations are not in the sidebar the collection
    // running beside it is reading. Tests navigate here and nowhere else.
    public string WebChatUrl =>
        _stack is { WebChatUrl: { Length: > 0 } url } ? url + _space : "";

    // Scoped to this collection's space, so a test says what the transcriber answers without
    // knowing that another collection is dictating beside it.
    public string Transcript
    {
        get => Stack.TranscriptFor(_space);
        set => Stack.SetTranscript(_space, value);
    }

    public int TranscriptionStatus
    {
        get => Stack.StatusFor(_space);
        set => Stack.SetTranscriptionStatus(_space, value);
    }

    public byte[]? LastAudio => Stack.LastAudioFor(_space);

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
// The split is by length, because a collection is what xUnit serializes and the run is as long as
// its longest chain. Three chains used to finish together at around forty-six seconds each — the
// chat classes, the gesture suites and the dictation cases — so shortening any one of them alone
// moved nothing.
//
// What used to make a split expensive was a browser per collection; the base class shares one now,
// so a collection costs its contexts and nothing else. What used to make the dictation cases
// unsplittable was the stub's single transcript, which is now kept per space.
[CollectionDefinition(WebChatE2ECollections.Chat)]
public class WebChatE2EChatCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.Dictation)]
public class WebChatE2EDictationCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.Hearth)]
public class WebChatE2EHearthCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.Flick)]
public class WebChatE2EFlickCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.DictationComposer)]
public class WebChatE2EDictationComposerCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.HearthTap)]
public class WebChatE2EHearthTapCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.Topics)]
public class WebChatE2ETopicsCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.Attachments)]
public class WebChatE2EAttachmentsCollection : ICollectionFixture<WebChatE2EFixture>;

public static class WebChatE2ECollections
{
    public const string Chat = "WebChatE2E.Chat";
    public const string Dictation = "WebChatE2E.Dictation";
    public const string Hearth = "WebChatE2E.Hearth";
    public const string Flick = "WebChatE2E.Flick";
    public const string DictationComposer = "WebChatE2E.DictationComposer";
    public const string HearthTap = "WebChatE2E.HearthTap";
    public const string Topics = "WebChatE2E.Topics";
    public const string Attachments = "WebChatE2E.Attachments";
}