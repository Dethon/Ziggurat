using System.Net;
using Domain.DTOs.Tunnel;

namespace McpServerWebSearch.Handoff;

// The pages a person sees under /browse: the live view of a handoff tab, and the answer a spent or
// expired link gets. WebChat's palette, light and dark, so the link opens on something familiar.
public static class HandoffPage
{
    private const string Style =
        """
        :root { --bg:#f3ede1; --panel:#fffaf1; --sunken:#ece4d4; --ink:#2a2118; --muted:#7a6a52; --line:#e6dbc6;
                --accent:#e9601f; --accent-ink:#fff3e9; --accent-soft:#fbeee2; --error:#c0492f; --ok:#5aa06e;
                --warn:#d9a441; color-scheme: light dark; }
        @media (prefers-color-scheme: dark) {
          :root { --bg:#171310; --panel:#211b15; --sunken:#2c241c; --ink:#f1e7d6; --muted:#b09d80; --line:#3a3027;
                  --accent:#f0712f; --accent-soft:#2a1d13; --error:#f08a70; }
        }
        * { box-sizing: border-box; }
        html, body { height: 100%; }
        body { margin:0; background:var(--bg); color:var(--ink); overscroll-behavior: none;
               font: 15px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
        main { max-width: 1280px; margin: 0 auto; padding: 12px 16px 24px; }
        main.live { height: 100vh; height: 100dvh; display: flex; flex-direction: column; gap: 8px;
                    padding: max(8px, env(safe-area-inset-top)) max(10px, env(safe-area-inset-right))
                             max(8px, env(safe-area-inset-bottom)) max(10px, env(safe-area-inset-left)); }
        .top { display:flex; align-items:center; gap:10px; }
        .top .who { flex:1; min-width:0; }
        h1 { font-size: 17px; font-weight: 600; margin: 0 0 4px; }
        .top h1 { margin: 0; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
        nav.way { max-width: 1280px; margin: 0 auto; padding: max(8px, env(safe-area-inset-top)) 16px 0; }
        nav.way a { color: var(--accent); font-weight: 600; text-decoration: none; display:inline-flex; min-height: 44px; align-items:center; }
        .lead { margin: 0; color: var(--muted); font-size: 13px; }
        .dot { display:inline-block; width:8px; height:8px; border-radius:50%; background:var(--muted); margin-right:6px; vertical-align: middle; }
        .dot.live { background: var(--ok); }
        .dot.away { background: var(--warn); }
        .view { position: relative; flex: 1; min-height: 0; background: var(--panel); border: 1px solid var(--line);
                border-radius: 10px; overflow: hidden; touch-action: none; }
        .view img { display:block; width:100%; height:100%; object-fit: contain; user-select:none;
                    -webkit-user-select:none; -webkit-user-drag:none; -webkit-touch-callout:none; outline: none; }
        .view .status { position:absolute; inset:0; display:flex; align-items:center; justify-content:center;
                        color: var(--muted); padding: 24px; text-align:center; background: color-mix(in srgb, var(--panel) 80%, transparent); }
        .bar { display:flex; gap:6px; align-items:center; }
        .bar input[type=text], .bar input[type=password] { flex: 1 1 auto; min-width:0; padding:10px 12px; border-radius:10px;
                    border:1px solid var(--line); background:var(--panel); color:var(--ink); font: 16px/1.2 inherit; }
        .bar input:focus { outline: 2px solid var(--accent); outline-offset: 0; }
        button { font: inherit; min-height: 44px; padding: 8px 12px; border-radius: 10px; border: 1px solid var(--line);
                 background: var(--panel); color: var(--ink); cursor: pointer; display:inline-flex; align-items:center;
                 justify-content:center; gap:6px; touch-action: manipulation; -webkit-tap-highlight-color: transparent; }
        button.primary { background: var(--accent); border-color: var(--accent); color: var(--accent-ink); font-weight: 600; padding: 8px 18px; }
        button:disabled { opacity: .55; cursor: default; }
        .options { display:flex; flex-wrap:wrap; gap: 4px 12px; align-items:center; color: var(--muted); font-size: 13px; }
        label.remember { display:flex; gap:8px; align-items:center; min-height: 32px; }
        .note { color: var(--muted); font-size: 13px; }
        .done { min-height: 100vh; min-height: 100dvh; display:grid; place-items:center; padding: 24px 16px; }
        .card { width:100%; max-width: 26rem; background: var(--panel); border:1px solid var(--line); border-radius: 18px; padding: 28px 22px; }
        .card h1 { white-space: normal; font-size: 20px; margin-bottom: 6px; }
        .card p { color: var(--muted); margin: 0 0 16px; }
        .card .mark { width: 52px; height: 52px; border-radius: 14px; display:grid; place-items:center;
                      background: var(--accent-soft); color: var(--accent); margin-bottom: 16px; }
        .card a.button, .card button { width: 100%; text-decoration: none; }
        a.button { display:inline-flex; align-items:center; justify-content:center; min-height: 44px; border-radius: 10px;
                   background: var(--accent); color: var(--accent-ink); font-weight: 600; }
        .site { display:flex; gap:12px; align-items:center; justify-content:space-between; padding:12px 14px;
                background: var(--panel); border:1px solid var(--line); border-radius:10px; margin-bottom:8px; }
        .error { color: var(--error); }
        @media (min-width: 900px) { main.live { padding: 16px 24px; } }
        """;

    // Drawn, never typed: the pages fetch nothing, so a glyph is inline SVG.
    private const string Backspace =
        """<svg aria-hidden="true" viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 4H8l-7 8 7 8h13a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2z"/><line x1="18" x2="12" y1="9" y2="15"/><line x1="12" x2="18" y1="9" y2="15"/></svg>""";

    private const string Link =
        """<svg aria-hidden="true" viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 17H7A5 5 0 0 1 7 7h2"/><path d="M15 7h2a5 5 0 1 1 0 10h-2"/><line x1="8" x2="16" y1="12" y2="12"/></svg>""";

    // A spent or expired link: what happened, and — since the installed chat app has no Back button —
    // a way to the chat, where a fresh link is one question away.
    public static string Gone(int minutes) =>
        Page("Link no longer valid",
            $"""
            <div class="done"><div class="card">
              <div class="mark">{Link}</div>
              <h1>This link has been used or has run out</h1>
              <p>Each link opens once and for {minutes} minutes. Ask again and you will get a fresh one.</p>
              <a class="button" href="/">Open the chat</a>
            </div></div>
            """);

    // The live view fills the screen: the site above, drawn at this screen's own size, and the controls
    // a phone needs around it — Done where a thumb reaches it, a keyboard row that the site's fields
    // also call up by themselves.
    public static string Viewer(string pages, string token, int minutes) => Page("Log in",
        $$"""
        <main class="live">
          <div class="top">
            <div class="who">
              <h1><span class="dot" id="dot"></span><span id="title">Log in</span></h1>
              <p class="lead">Log in, then press Done. What you type goes to the site, never to the agent.</p>
            </div>
            <button type="button" class="primary" id="done" disabled>Done</button>
          </div>
          <div class="view" id="view">
            <img id="frame" alt="The page you are logging in on" tabindex="0">
            <div class="status" id="status" role="status">Connecting…</div>
          </div>
          <div class="bar">
            <input type="text" id="keys" placeholder="Type here" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" enterkeyhint="go" aria-label="Type into the page">
            <button type="button" data-key="Backspace" aria-label="Backspace">{{Backspace}}</button>
            <button type="button" data-key="Tab">Tab</button>
            <button type="button" data-key="Enter">Enter</button>
          </div>
          <div class="options" id="actions">
            <label class="remember"><input type="checkbox" id="remember" checked> Remember this login</label>
            <span class="note" id="remember-note" hidden>Remembering is off on this server: this login lasts until the browser restarts.</span>
            <span class="note">Passkeys can't be used here. If the site asks for one, choose another way to sign in.</span>
          </div>
        </main>
        {{Data(pages, token, minutes)}}<script>{{BrowsePagePolicy.HandoffScript}}</script>
        """);

    public static string Logins(string pages, string token, int minutes) => Page("Your logins",
        $$"""
        <nav class="way"><a href="/">Open the chat</a></nav>
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
         <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover, interactive-widget=resizes-content">
         <meta name="theme-color" content="#f3ede1" media="(prefers-color-scheme: light)">
         <meta name="theme-color" content="#171310" media="(prefers-color-scheme: dark)">
         <meta name="referrer" content="no-referrer">
         <title>{WebUtility.HtmlEncode(title)}</title>
         <style>{Style}</style></head>
         <body>{body}</body></html>
         """;
}