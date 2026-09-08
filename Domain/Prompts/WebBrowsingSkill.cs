namespace Domain.Prompts;

// The doing rules of the web, loaded when a request reaches for the browser. That a url comes from
// a search and that no call is a probe stay in the web prompt, because both are decided before
// the first call — and a probe is the call that would precede any load (docs/adr/0039).
public static class WebBrowsingSkill
{
    public const string Name = "web-browsing";

    // The whole trigger: the one line about this skill that is in every turn.
    public const string Description =
        "Any task on the web: searching for something, reading a page or its pictures, filling or submitting a form, navigating a site (\"look up how long the gazpacho rests\", \"book a place on that course\", \"what does the site say about opening hours\"). The tools at a glance, the read-then-act workflow, chaining actions from a snapshot's refs, error recovery, and how a written or spoken answer cites what it found.";

    public const string Body =
        """
        ### Tools at a glance

        - **web_search** — find candidate URLs before navigating; don't guess URLs.
        - **web_browse** — load a URL and read its content as markdown.
        - **web_snapshot** — see the current page's interactive elements with refs.
        - **web_action** — interact with an element (or navigate back) by ref.
        - **view_image** — look at pictures on the page you browsed, by their image refs.

        See each tool's own description for arguments, action verbs, and defaults — don't restate them
        from memory.

        ### Core Workflow

        **Reading a page.** Call web_browse. If the response is truncated or you need a specific
        region, narrow it (the tool description shows how) before falling back to a second call.
        When the page you opened for an answer arrives truncated, read its remainder (offset)
        before opening a different page — the answer is usually in the tail you have not read,
        and another page's snippet is a promise, not the page.

        **Looking at a picture.** web_browse lists each image where it sits in the page text, as
        `[image i-1: what the page calls it]`. Pass those refs to view_image to see the pictures
        themselves — several in one call. Ask when the answer is in the picture rather than the
        prose: a chart's numbers, a scan with no text layer, what a product actually looks like.
        Image refs (i-1) and element refs (e-1) are separate: view_image takes the first, web_action
        the second, and each refuses the other by name.

        **Interacting with a page.** Load with web_browse using snapshot=true to get content and refs
        in a single call, then chain web_action calls. Each web_action returns a diff with new refs —
        use those for the next action and only call web_snapshot again if the diff doesn't show what
        you need. (Use a standalone web_snapshot only when you need a fresh tree mid-session.)

        **Autocomplete / combobox fields.** Type the value to trigger the page's JS handler; if a
        dropdown appears in the diff, click the option you want, otherwise confirm the selection
        with the appropriate key press.

        **Hover menus / tooltips.** Hover the trigger first; the diff reveals the menu refs to click.

        **Multi-page navigation.** Click links/buttons normally. Going back is web_action's back
        action, never a second web_browse of a page you already visited — knowing its URL does not
        change that; a re-browse starts the page over and loses its state.

        ### Key Principles

        1. **One snapshot, then chain actions.** Snapshot is expensive context; reuse the refs from
           each action's diff before snapshotting again.
        2. **web_browse for content, web_snapshot for structure.** Don't call both for the same
           purpose — text vs. element refs are distinct goals.
        3. **Type vs. fill.** Use type when the field reacts to keystrokes (autocomplete, validation
           on input); use fill when you just need the value set.
        4. **Read the diff.** Added elements (`+`) and removed (`-`) tell you exactly what changed;
           new refs there are valid for the next action.
        5. **Verify silently.** Verify each action produced the expected change before the next one;
           verification is internal — do not report the steps.

        ### Error Recovery

        | Situation                | Strategy                                                                |
        |--------------------------|-------------------------------------------------------------------------|
        | Content truncated        | Paginate or narrow the extraction (see web_browse description).         |
        | Can't find element       | Re-snapshot to see what's actually there.                               |
        | Autocomplete not opening | Type the full value, then confirm with a key press.                     |
        | Lazy-loaded content      | Re-browse with scroll-to-load enabled (see web_browse description).     |
        | Session expired          | Re-browse to start a fresh session.                                     |
        | Modal blocking content   | Usually auto-dismissed; otherwise find a close button via snapshot.     |
        | Hidden hover content     | Hover the trigger to reveal it.                                         |
        | Need to go back          | Use web_action's back rather than re-browsing the previous URL.         |
        | Click times out on a ref | Retry once with the force option (see web_action description) only if you're certain the ref is correct. |

        ### Response Style

        - Answer the question from what you found; never dump raw page content.
        - A written reply names the url of the page it answered from — the user has to be able to
          check the source. A reply that is read aloud never carries a url.
        - If content is partial, fetch the missing part once, then answer with what you have; if you
          still cannot, say so in one clause — don't offer to get more.
        - In a written reply, format extracted data as a table or list; when your reply is read aloud,
          speak the values only.

        ### Limitations

        - Cannot access pages requiring CAPTCHA (unless CapSolver configured).
        - Cannot interact with file download dialogs.
        - Session is per-conversation — resets between conversations.
        - Some sites may block automated access.
        """;

    public static readonly SkillText Text = new(Name, Description, Body);

    // The trigger claim: a request of this kind loads the skill. Cited by every web scenario, so a
    // skill nobody loads shows as a red description rather than a red body.
    public static readonly PromptClaim LoadsForAWebTask =
        new("web-browsing.loads-for-a-web-task",
            "A request to search, read a page, act on one or navigate a site loads the web-browsing skill before the first web call.");

    // Every falsifiable statement the prose above makes. They split into two: where an answer
    // comes from, and how an interaction is aimed — and only the second fails loudly, because a
    // ref that was never in a snapshot simply misses.
    public static readonly PromptClaim AnswerComesFromWhatWasRead =
        new("web-browsing.answer-comes-from-what-was-read",
            "The answer states what the page said, not what the search result summarised.");

    public static readonly PromptClaim RawContentIsNeverDumped =
        new("web-browsing.raw-content-is-never-dumped",
            "A reply answers the question rather than pasting the page back.");

    public static readonly PromptClaim RefsComeFromASnapshot =
        new("web-browsing.refs-come-from-a-snapshot",
            "An element is acted on by a ref that came from a snapshot of the page, taken before the action.");

    public static readonly PromptClaim ActionsChainFromTheDiff =
        new("web-browsing.actions-chain-from-the-diff",
            "Refs from an action's diff are reused rather than a fresh snapshot being taken between every action.");

    public static readonly PromptClaim BrowseReadsAndSnapshotStructures =
        new("web-browsing.browse-reads-and-snapshot-structures",
            "Content is read with a browse and structure with a snapshot, never both for the same purpose.");

    public static readonly PromptClaim TypeReactsAndFillSets =
        new("web-browsing.type-reacts-and-fill-sets",
            "A field that reacts to keystrokes is typed into; one that only needs a value is filled.");

    public static readonly PromptClaim UrlsAreCitedOnlyInWriting =
        new("web-browsing.urls-are-cited-only-in-writing",
            "A source url appears in a written reply and never in one that is read aloud.");

    public static readonly PromptClaim StepsAreNotReported =
        new("web-browsing.steps-are-not-reported",
            "The reply carries the answer rather than an account of the pages and clicks it took.");

    public static readonly PromptClaim PartialContentIsFetchedOnce =
        new("web-browsing.partial-content-is-fetched-once",
            "Truncated content is fetched once more and then answered from, never offered to be fetched again.");

    public static readonly PromptClaim BackIsAnAction =
        new("web-browsing.back-is-an-action",
            "Going back to the previous page is the browser's own back rather than a second browse of its url.");

    public static readonly IReadOnlyList<PromptClaim> Claims =
    [
        LoadsForAWebTask,
        AnswerComesFromWhatWasRead,
        RawContentIsNeverDumped,
        RefsComeFromASnapshot,
        ActionsChainFromTheDiff,
        BrowseReadsAndSnapshotStructures,
        TypeReactsAndFillSets,
        UrlsAreCitedOnlyInWriting,
        StepsAreNotReported,
        PartialContentIsFetchedOnce,
        BackIsAnAction
    ];
}