using System.Net;

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

    public static string Viewer(string token, int minutes) => Page("Log in",
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
        <script>
        (() => {
          const token = {{System.Text.Json.JsonSerializer.Serialize(token)}};
          const frame = document.getElementById('frame');
          const status = document.getElementById('status');
          const keys = document.getElementById('keys');
          const done = document.getElementById('done');
          let ws = null, url = null, finished = false;

          const say = (text, error) => {
            status.textContent = text; status.style.display = text ? 'flex' : 'none';
            status.classList.toggle('error', !!error);
          };
          const send = message => { if (ws && ws.readyState === 1) ws.send(JSON.stringify(message)); };
          const at = e => {
            const r = frame.getBoundingClientRect();
            return { x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height };
          };
          const button = e => e.button === 2 ? 'right' : e.button === 1 ? 'middle' : 'left';

          frame.addEventListener('contextmenu', e => e.preventDefault());
          frame.addEventListener('pointerdown', e => { e.preventDefault(); frame.focus(); send({ type: 'mousedown', button: button(e), ...at(e) }); });
          frame.addEventListener('pointerup', e => { e.preventDefault(); send({ type: 'mouseup', button: button(e), ...at(e) }); });
          let lastMove = 0;
          frame.addEventListener('pointermove', e => {
            const now = Date.now(); if (now - lastMove < 50) return; lastMove = now;
            send({ type: 'mousemove', ...at(e) });
          });
          frame.addEventListener('wheel', e => { e.preventDefault(); send({ type: 'wheel', dx: e.deltaX, dy: e.deltaY }); }, { passive: false });

          // On a desktop the picture takes the keyboard like a page would; on a phone the text box
          // brings the keyboard up and hands over whatever it composes.
          const special = k => k.length > 1;
          frame.addEventListener('keydown', e => {
            if (e.metaKey || e.ctrlKey) return;
            e.preventDefault();
            if (special(e.key)) send({ type: 'keydown', key: e.key }); else send({ type: 'text', value: e.key });
          });
          frame.addEventListener('keyup', e => { if (special(e.key)) { e.preventDefault(); send({ type: 'keyup', key: e.key }); } });
          keys.addEventListener('keydown', e => {
            if (special(e.key) && e.key !== 'Unidentified' && e.key !== 'Process') {
              e.preventDefault(); send({ type: 'keydown', key: e.key }); send({ type: 'keyup', key: e.key });
            }
          });
          keys.addEventListener('input', () => { if (keys.value) { send({ type: 'text', value: keys.value }); keys.value = ''; } });
          document.querySelectorAll('[data-key]').forEach(b => b.addEventListener('click', () => {
            send({ type: 'keydown', key: b.dataset.key }); send({ type: 'keyup', key: b.dataset.key });
          }));

          const finish = text => {
            finished = true; if (ws) ws.close();
            document.querySelector('main').innerHTML = '<div class="done"><h1></h1><p class="lead"></p></div>';
            document.querySelector('.done h1').textContent = text[0];
            document.querySelector('.done p').textContent = text[1];
          };

          done.addEventListener('click', async () => {
            done.disabled = true;
            const remember = document.getElementById('remember');
            const answer = await fetch('/browse/handoff/done', {
              method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ remember: remember ? remember.checked : true })
            });
            if (answer.ok) finish(['You are logged in', 'The agent is carrying on with what you asked. You can close this page.']);
            else finish(['This login has ended', 'Ask again for a fresh link.']);
          });

          const connect = () => {
            ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/browse/handoff/live');
            ws.binaryType = 'blob';
            ws.onmessage = e => {
              const next = URL.createObjectURL(e.data);
              frame.onload = () => { if (url) URL.revokeObjectURL(url); url = next; say(''); };
              frame.src = next;
            };
            ws.onclose = () => { if (!finished) finish(['This login has ended', 'It ran out of time or the browser restarted. Ask again for a fresh link.']); };
          };

          (async () => {
            const claim = await fetch('/browse/handoff/' + encodeURIComponent(token) + '/claim', { method: 'POST' });
            if (!claim.ok) { finish(['This link has been used or has run out', 'Each link opens once and for {{minutes}} minutes. Ask again and you will get a fresh one.']); return; }
            const { site, canRemember } = await claim.json();
            document.getElementById('title').textContent = 'Log in to ' + site;
            if (!canRemember) {
              const box = document.getElementById('remember');
              box.checked = false; box.disabled = true;
              document.getElementById('remember-note').hidden = false;
            }
            done.disabled = false;
            connect();
          })();
        })();
        </script>
        """);

    public static string Logins(string token, int minutes) => Page("Your logins",
        $$"""
        <main>
          <h1>Sites the agent can reach as you</h1>
          <p class="lead">Forget one and the agent is logged out of it, now and after a restart.</p>
          <div id="sites"><p class="lead">Loading...</p></div>
        </main>
        <script>
        (() => {
          const token = {{System.Text.Json.JsonSerializer.Serialize(token)}};
          const list = document.getElementById('sites');
          const when = at => at ? new Date(at).toLocaleString() : 'not yet';
          const alertNote = (row, text) => { const n = document.createElement('div'); n.className = 'note error'; n.textContent = text; row.append(n); };
          const say = (title, text) => {
            document.querySelector('main').innerHTML = '<div class="done"><h1></h1><p class="lead"></p></div>';
            document.querySelector('.done h1').textContent = title;
            document.querySelector('.done p').textContent = text;
          };
          const render = sites => {
            list.innerHTML = '';
            if (!sites.length) { list.innerHTML = '<p class="lead">The agent is not logged into anything as you.</p>'; return; }
            for (const s of sites) {
              const row = document.createElement('div'); row.className = 'site';
              const text = document.createElement('div');
              const name = document.createElement('strong'); name.textContent = s.site;
              const meta = document.createElement('div'); meta.className = 'note';
              meta.textContent = (s.remembered ? 'Remembered' : 'Until the browser restarts') + ' · last used ' + when(s.lastUsedAt);
              text.append(name, meta);
              const forget = document.createElement('button'); forget.type = 'button'; forget.textContent = 'Forget';
              forget.addEventListener('click', async () => {
                forget.disabled = true;
                const answer = await fetch('/browse/logins/forget', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ site: s.site }) });
                if (answer.ok) load();
                else if (answer.status === 409) { forget.disabled = false; alertNote(row, 'Finish or leave the login you have open first.'); }
                else say('This page has run out', 'Ask the agent for a fresh link to your logins.');
              });
              row.append(text, forget); list.append(row);
            }
          };
          const load = async () => {
            const answer = await fetch('/browse/logins/sites');
            if (!answer.ok) { say('This page has run out', 'Ask the agent for a fresh link to your logins.'); return; }
            render(await answer.json());
          };
          (async () => {
            const claim = await fetch('/browse/logins/' + encodeURIComponent(token) + '/claim', { method: 'POST' });
            if (!claim.ok) { say('This link has been used or has run out', 'Each link opens once and for {{minutes}} minutes. Ask again and you will get a fresh one.'); return; }
            load();
          })();
        })();
        </script>
        """);

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