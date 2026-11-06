using System.Security.Cryptography;
using System.Text;

namespace Domain.DTOs.Tunnel;

// What a browse page may do on the main host. The page is served by the web search of the household
// the link names, and that server runs on the household's own machine, so its answer is not the
// deployment's to trust: under /browse it is same-origin with the chat client and the admin API, and a
// script of its own would act as whoever opened the link. The relay (BrowsePages) therefore sends every
// answer under /browse with this policy in place of whatever the server said — the deployment's own
// scripts alone may run, pinned by hash — and keeps a cookie only when it is one of the page's two view
// cookies on its own household's path. Both ends read this one file: web search renders exactly these
// scripts, and the relay pins exactly their hashes.
public static class BrowsePagePolicy
{
    public const string ViewCookie = "zg_handoff";
    public const string LoginsCookie = "zg_logins";

    public static readonly IReadOnlyList<string> ViewCookies = [ViewCookie, LoginsCookie];

    // The id of the element whose JSON the scripts read their link's values from: data, never code, so
    // a script stays the same text for every link and its hash can be pinned.
    public const string DataElementId = "page-data";

    public static readonly string HandoffScript =
        """
        (() => {
          const data = JSON.parse(document.getElementById('page-data').textContent);
          const token = data.token, pages = data.pages, minutes = data.minutes;
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
            const answer = await fetch(pages + '/handoff/done', {
              method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ remember: remember ? remember.checked : true })
            });
            if (answer.ok) finish(['You are logged in', 'The agent is carrying on with what you asked. You can close this page.']);
            else finish(['This login has ended', 'Ask again for a fresh link.']);
          });

          const connect = () => {
            ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + pages + '/handoff/live');
            ws.binaryType = 'blob';
            ws.onmessage = e => {
              const next = URL.createObjectURL(e.data);
              frame.onload = () => { if (url) URL.revokeObjectURL(url); url = next; say(''); };
              frame.src = next;
            };
            ws.onclose = () => { if (!finished) finish(['This login has ended', 'It ran out of time or the browser restarted. Ask again for a fresh link.']); };
          };

          (async () => {
            const claim = await fetch(pages + '/handoff/' + encodeURIComponent(token) + '/claim', { method: 'POST' });
            if (!claim.ok) { finish(['This link has been used or has run out', 'Each link opens once and for ' + minutes + ' minutes. Ask again and you will get a fresh one.']); return; }
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
        """.ReplaceLineEndings("\n");

    public static readonly string LoginsScript =
        """
        (() => {
          const data = JSON.parse(document.getElementById('page-data').textContent);
          const token = data.token, pages = data.pages, minutes = data.minutes;
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
              meta.textContent = (s.remembered ? 'Remembered' : 'Until the browser restarts') + ' · logged in ' + when(s.handedOffAt) + ' · last used ' + when(s.lastUsedAt);
              text.append(name, meta);
              const others = (s.domains || []).filter(d => d !== s.site);
              if (others.length) { const via = document.createElement('div'); via.className = 'note'; via.textContent = 'Also covers ' + others.join(', '); text.append(via); }
              const forget = document.createElement('button'); forget.type = 'button'; forget.textContent = 'Forget';
              forget.addEventListener('click', async () => {
                forget.disabled = true;
                const answer = await fetch(pages + '/logins/forget', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ site: s.site }) });
                if (answer.ok) load();
                else if (answer.status === 409) { forget.disabled = false; alertNote(row, 'Finish or leave the login you have open first.'); }
                else say('This page has run out', 'Ask the agent for a fresh link to your logins.');
              });
              row.append(text, forget); list.append(row);
            }
          };
          const load = async () => {
            const answer = await fetch(pages + '/logins/sites');
            if (!answer.ok) { say('This page has run out', 'Ask the agent for a fresh link to your logins.'); return; }
            render(await answer.json());
          };
          (async () => {
            const claim = await fetch(pages + '/logins/' + encodeURIComponent(token) + '/claim', { method: 'POST' });
            if (!claim.ok) { say('This link has been used or has run out', 'Each link opens once and for ' + minutes + ' minutes. Ask again and you will get a fresh one.'); return; }
            load();
          })();
        })();
        """.ReplaceLineEndings("\n");

    // The scripts a browse page may run, as CSP hash sources. A kit on an older build whose scripts
    // differ shows its page without them until it is updated; when these scripts change, the last
    // release's hashes join this list for one release so a kit a step behind keeps working.
    // A browser hashes the script as its parser read it, which turns every CRLF into LF: the scripts
    // are held with LF line ends whatever the checkout gave the source.
    public static readonly IReadOnlyList<string> ScriptHashes = [HashOf(HandoffScript), HashOf(LoginsScript)];

    // No script but the pinned ones, no plugin, no form, no frame, no base; pictures only from the live
    // view's blobs; requests only to this origin, which the pinned scripts keep under the household's
    // pages. The page's own inline style is allowed: with nothing else loadable it reaches nobody.
    public static string ContentSecurityPolicy { get; } =
        "default-src 'none'; " +
        $"script-src {string.Join(' ', ScriptHashes.Select(h => $"'{h}'"))}; " +
        "style-src 'unsafe-inline'; img-src blob: data:; connect-src 'self'; " +
        "form-action 'none'; base-uri 'none'; frame-ancestors 'none'; object-src 'none'";

    public static string HashOf(string script) =>
        "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
}