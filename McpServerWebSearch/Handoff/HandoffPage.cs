using System.Net;
using Domain.DTOs.Tunnel;

namespace McpServerWebSearch.Handoff;

// The pages a person sees under /browse: the live view of a handoff tab, and the answer a spent or
// expired link gets. WebChat's palette, light and dark, so the link opens on something familiar.
public static class HandoffPage
{
    private const string Style =
        """
        :root { --bg:#f3ede1; --panel:#fffaf1; --ink:#2a2118; --muted:#7a6a52; --line:#e6dbc6;
                --accent:#e9601f; --accent-ink:#fff3e9; --error:#c0492f; color-scheme: light dark; }
        @media (prefers-color-scheme: dark) {
          :root { --bg:#171310; --panel:#211b15; --ink:#f1e7d6; --muted:#b09d80; --line:#3a3027; }
        }
        * { box-sizing: border-box; }
        body { margin:0; background:var(--bg); color:var(--ink);
               font: 15px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
        main { max-width: 1280px; margin: 0 auto; padding: 12px 16px 24px; }
        h1 { font-size: 17px; font-weight: 600; margin: 4px 0 2px; }
        p.lead { margin: 0 0 12px; color: var(--muted); }
        .view { position: relative; background: var(--panel); border: 1px solid var(--line);
                border-radius: 10px; overflow: hidden; touch-action: none; }
        .view img { display:block; width:100%; height:auto; user-select:none; -webkit-user-drag:none;
                    cursor: default; outline: none; }
        .view .status { position:absolute; inset:0; display:flex; align-items:center; justify-content:center;
                        color: var(--muted); padding: 24px; text-align:center; }
        .bar { display:flex; flex-wrap:wrap; gap:8px; align-items:center; margin-top:12px; }
        .bar input[type=text] { flex: 1 1 220px; min-width:0; padding:10px 12px; border-radius:8px;
                    border:1px solid var(--line); background:var(--panel); color:var(--ink); font:inherit; }
        button { font: inherit; padding: 10px 14px; border-radius: 8px; border: 1px solid var(--line);
                 background: var(--panel); color: var(--ink); cursor: pointer; }
        button.primary { background: var(--accent); border-color: var(--accent); color: var(--accent-ink);
                         font-weight: 600; }
        button:disabled { opacity: .55; cursor: default; }
        label.remember { display:flex; gap:8px; align-items:center; color: var(--muted); margin-right:auto; }
        .note { color: var(--muted); font-size: 13px; flex-basis: 100%; }
        .done { padding: 32px 0; }
        .site { display:flex; gap:12px; align-items:center; justify-content:space-between; padding:12px 14px;
                background: var(--panel); border:1px solid var(--line); border-radius:10px; margin-bottom:8px; }
        .error { color: var(--error); }
        """;

    public static string Gone(int minutes) =>
        Page("Link no longer valid",
            $"""
            <main class="done"><h1>This link has been used or has run out</h1>
            <p class="lead">Each link opens once and for {minutes} minutes. Ask again and you will get a fresh one.</p></main>
            """);

    public static string Viewer(string pages, string token, int minutes) => Page("Log in",
        $$"""
        <main>
          <h1 id="title">Log in</h1>
          <p class="lead">This is the agent's browser. Log in as you normally would, then press Done. What you type goes to the site, never to the agent.</p>
          <div class="view" id="view">
            <img id="frame" alt="The page you are logging in on" tabindex="0">
            <div class="status" id="status">Connecting...</div>
          </div>
          <div class="bar">
            <input type="text" id="keys" placeholder="Tap here to type" autocomplete="off" autocapitalize="off" spellcheck="false" aria-label="Type into the page">
            <button type="button" data-key="Backspace">Backspace</button>
            <button type="button" data-key="Tab">Tab</button>
            <button type="button" data-key="Enter">Enter</button>
          </div>
          <div class="bar" id="actions">
            <label class="remember"><input type="checkbox" id="remember" checked> Remember this login</label>
            <span class="note" id="remember-note" hidden>Remembering is off on this server: this login lasts until the browser restarts.</span>
            <button type="button" class="primary" id="done" disabled>Done</button>
          </div>
        </main>
        {{Data(pages, token, minutes)}}<script>{{BrowsePagePolicy.HandoffScript}}</script>
        """);

    public static string Logins(string pages, string token, int minutes) => Page("Your logins",
        $$"""
        <main>
          <h1>Logins the agent can use as you</h1>
          <p class="lead">Forget one and the agent is logged out of it, now and after a restart. A sign-in service another login still uses stays.</p>
          <div id="sites"><p class="lead">Loading...</p></div>
        </main>
        {{Data(pages, token, minutes)}}<script>{{BrowsePagePolicy.LoginsScript}}</script>
        """);

    // The link's values as data the pinned script reads, never as code: the serializer escapes '<', so
    // nothing in a value can close the element.
    private static string Data(string pages, string token, int minutes) =>
        $"<script type=\"application/json\" id=\"{BrowsePagePolicy.DataElementId}\">"
        + System.Text.Json.JsonSerializer.Serialize(new { token, pages, minutes })
        + "</script>";

    private static string Page(string title, string body) =>
        $"""
         <!DOCTYPE html>
         <html lang="en"><head><meta charset="utf-8">
         <meta name="viewport" content="width=device-width, initial-scale=1">
         <meta name="referrer" content="no-referrer">
         <title>{WebUtility.HtmlEncode(title)}</title>
         <style>{Style}</style></head>
         <body>{body}</body></html>
         """;
}