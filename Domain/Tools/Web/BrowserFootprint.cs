namespace Domain.Tools.Web;

// What the browse tools say their calls touch, as the server declares it in each tool's `_meta`
// (FootprintTemplate). The shared thing is the conversation's browse session: its tabs, the refs
// stamped on them, and which tab is current.
//
// A ref routes to whichever tab stamped it and the ref-less operations act on the current tab,
// and only the server knows which tab that is — so every call that acts through a ref or on the
// current tab claims the whole session, and two of them in one message happen in the order
// written. That is the race this exists for: two clicks on one tab, each navigating away, or a
// click beside a `back`.
//
// A browse claims the one page it opens. Browses of different urls land on different tabs and
// still run at once, as the tab pool was built for; the same url twice reloads one tab and is
// ordered, and a browse is ordered against any action or snapshot beside it. What that leaves
// undecided is which tab is current after several browses in one message — the last to land,
// not the last written — and it matters only to a ref-less `back` or snapshot in a later message.
public static class BrowserFootprint
{
    public const string Session = """{"changes":["browser"]}""";

    public const string Page = """{"changes":["browser/page/{url}"]}""";
}