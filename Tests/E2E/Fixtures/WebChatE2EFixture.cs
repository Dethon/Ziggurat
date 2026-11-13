using Domain.DTOs.Channel;
using Microsoft.Playwright;

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

    // The default space, which is where a review copy's topic is minted (another channel's
    // conversation carries no space of WebChat's). Only the Sugary collection goes there.
    public string DefaultSpaceUrl => _stack?.WebChatUrl ?? "";

    public string SugaryUrl => _stack?.SugaryUrl ?? "";

    public string SugaryMcpEndpoint => _stack?.SugaryMcpEndpoint ?? "";

    // The account a user index signs in as, which a test reaching the Sugary server past the proxy
    // names in the session check's headers.
    public Task<Account> AccountForAsync(int userIndex) => Stack.AccountForAsync(userIndex);

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

    // Asking for one makes the next one in the background, so a collection's tests find their user
    // already made.
    public int NextUserIndex()
    {
        var next = Interlocked.Increment(ref _userIndex);
        Stack.Prefetch(IndexAt(next + 1));
        return IndexAt(next);
    }

    private int IndexAt(int position) => _userBlock + (int)((uint)position % WebChatStack.UsersPerCollection);

    // Every page of the chat client is behind the session check, so a test's page carries its
    // user's session before it opens anything: the account for that index, made on first asking,
    // signed in by a minted session rather than a passkey ceremony.
    public async Task<IPage> CreateSignedInPageAsync(int userIndex, bool hasTouch = false, bool isMobile = false) =>
        await CreatePageSignedInAsync(await Stack.SessionForAsync(userIndex), hasTouch, isMobile);

    // A page signed in by a session the test minted itself, for a person the stack's users are not —
    // an owner, say, since every one of them is a member.
    public async Task<IPage> CreatePageSignedInAsync(
        string session, bool hasTouch = false, bool isMobile = false, bool withoutServiceWorker = false)
    {
        var page = await CreatePageAsync(hasTouch, isMobile, withoutServiceWorker);
        await page.Context.AddCookiesAsync(
        [
            new Cookie
            {
                Name = SessionCookieName,
                Value = session,
                Url = Stack.WebChatUrl,
                HttpOnly = true,
                SameSite = SameSiteAttribute.Lax
            }
        ]);
        return page;
    }

    public const string SessionCookieName = "ziggurat_session";

    public static string NameOf(int userIndex) => WebChatStack.NameOf(userIndex);

    // A person of the home household a test makes for itself, by name and role (owner or member):
    // their id and a signed-in session.
    public Task<(string UserId, string Session)> PersonAsync(string name, string role) => Stack.PersonAsync(name, role);

    // A person of the stack's second household, for a test that changes who that household's people
    // are: the hub has every open page of a household renew its connection on such a change, so it is
    // made where no other collection has a page open.
    public Task<(string UserId, string Session)> PersonElsewhereAsync(string name, string role) =>
        Stack.PersonAsync(name, role, elsewhere: true);

    // The accounts service's commands, for a test that needs a link or a command as the operator runs it.
    public Task<(int Exit, string Output)> AccountsAsync(params string[] args) => Stack.AccountsAsync(args);

    protected override Task BuildImagesAsync(CancellationToken ct) => WebChatStack.EnsureImagesAsync(ct);

    protected override async Task StartContainersAsync(CancellationToken ct)
    {
        _stack = await WebChatStack.AcquireAsync(ct);
        (_userBlock, _space) = _stack.ReserveSlice();
        _stack.Prefetch(IndexAt(0));
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

[CollectionDefinition(WebChatE2ECollections.DictationMicrophone)]
public class WebChatE2EDictationMicrophoneCollection : ICollectionFixture<WebChatE2EFixture>;

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

[CollectionDefinition(WebChatE2ECollections.Sugary)]
public class WebChatE2ESugaryCollection : ICollectionFixture<WebChatE2EFixture>;

// The household's admin pages but its settings: each class makes its own owner and changes only what
// it made, so they never needed the chat classes' line — only the settings page, which renames the
// household a chat case reads, stays in Chat.
[CollectionDefinition(WebChatE2ECollections.Admin)]
public class WebChatE2EAdminCollection : ICollectionFixture<WebChatE2EFixture>;

// Two or more people of their own each, in a topic of their own: shared topics, a sees-all grant
// withdrawn, a session ended elsewhere.
[CollectionDefinition(WebChatE2ECollections.People)]
public class WebChatE2EPeopleCollection : ICollectionFixture<WebChatE2EFixture>;

// The chat's turns on the real model, out of the Chat collection, which keeps only the cases reading
// the household's name and agents beside the settings page that changes them.
[CollectionDefinition(WebChatE2ECollections.Conversation)]
public class WebChatE2EConversationCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.SignIn)]
public class WebChatE2ESignInCollection : ICollectionFixture<WebChatE2EFixture>;

// The admin pages and the people classes, each split once more by length: their classes make their
// own owners and people and change only what they made, so neither half waits on the other.
[CollectionDefinition(WebChatE2ECollections.AdminPages)]
public class WebChatE2EAdminPagesCollection : ICollectionFixture<WebChatE2EFixture>;

[CollectionDefinition(WebChatE2ECollections.Sharing)]
public class WebChatE2ESharingCollection : ICollectionFixture<WebChatE2EFixture>;

// The seventeenth, which fills the slices the stack has (WebChatCollectionSliceTests): a further
// collection needs a space name and CollectionSlices raised.
[CollectionDefinition(WebChatE2ECollections.Members)]
public class WebChatE2EMembersCollection : ICollectionFixture<WebChatE2EFixture>;

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
    public const string DictationMicrophone = "WebChatE2E.DictationMicrophone";
    public const string Sugary = "WebChatE2E.Sugary";
    public const string Admin = "WebChatE2E.Admin";
    public const string People = "WebChatE2E.People";
    public const string Conversation = "WebChatE2E.Conversation";
    public const string SignIn = "WebChatE2E.SignIn";
    public const string AdminPages = "WebChatE2E.AdminPages";
    public const string Sharing = "WebChatE2E.Sharing";
    public const string Members = "WebChatE2E.Members";
}