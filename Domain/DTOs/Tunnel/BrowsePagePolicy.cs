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
          const view = document.getElementById('view');
          const frame = document.getElementById('frame');
          const status = document.getElementById('status');
          const dot = document.getElementById('dot');
          const keys = document.getElementById('keys');
          const done = document.getElementById('done');
          let ws = null, url = null, finished = false, claimed = false, from = null, retry = 0, retryTimer = null;
          let fields = [], sent = null, touch = null, focusOnTouchEnd = false;

          const say = (text, error) => {
            status.textContent = text; status.style.display = text ? 'flex' : 'none';
            status.classList.toggle('error', !!error);
          };
          const send = message => { if (ws && ws.readyState === 1) ws.send(JSON.stringify(message)); };

          // The picture keeps the page's shape inside its box, so a point is a fraction of the picture drawn.
          const drawn = () => {
            const r = frame.getBoundingClientRect(), w = frame.naturalWidth, h = frame.naturalHeight;
            if (!w || !h) return r;
            const scale = Math.min(r.width / w, r.height / h), dw = w * scale, dh = h * scale;
            return { left: r.left + (r.width - dw) / 2, top: r.top + (r.height - dh) / 2, width: dw, height: dh };
          };
          const at = e => {
            const r = drawn();
            return { x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height };
          };
          const inside = p => p.x >= 0 && p.x <= 1 && p.y >= 0 && p.y <= 1;
          const button = e => e.button === 2 ? 'right' : e.button === 1 ? 'middle' : 'left';

          // The site is drawn at the size it is shown at: said when the view opens and when its width changes
          // (a phone turned), never when the keyboard only shortens it, or the site reflows under the field.
          const size = () => ({ width: Math.round(view.clientWidth), height: Math.round(view.clientHeight) });
          const sendSize = () => { sent = size(); send({ type: 'resize', ...sent }); };
          let resizing = null;
          window.addEventListener('resize', () => {
            clearTimeout(resizing);
            resizing = setTimeout(() => { if (sent && Math.abs(size().width - sent.width) > 40) sendSize(); }, 300);
          });

          // The keyboard the site's field wants: a tap on a field brings it up at once, of its kind.
          const keyboards = {
            password: { type: 'password' }, email: { mode: 'email' }, tel: { mode: 'tel' }, number: { mode: 'numeric' },
            'one-time-code': { mode: 'numeric', auto: 'one-time-code' }, url: { mode: 'url' }
          };
          const keyboardFor = kind => {
            const k = keyboards[kind] || {};
            keys.type = k.type || 'text';
            keys.inputMode = k.mode || 'text';
            keys.autocomplete = k.auto || 'off';
          };
          const fieldAt = p => fields.find(f =>
            p.x >= f.x - 0.01 && p.x <= f.x + f.width + 0.01 && p.y >= f.y - 0.01 && p.y <= f.y + f.height + 0.01);

          frame.addEventListener('contextmenu', e => e.preventDefault());

          // A mouse is a mouse. A finger is a tap or a drag: a tap clicks where it lands, a drag scrolls.
          frame.addEventListener('pointerdown', e => {
            e.preventDefault();
            if (e.pointerType === 'touch') {
              touch = { id: e.pointerId, x: e.clientX, y: e.clientY, lastX: e.clientX, lastY: e.clientY, scrolling: false };
              return;
            }
            frame.focus(); send({ type: 'mousedown', button: button(e), ...at(e) });
          });
          let lastMove = 0, pending = { dx: 0, dy: 0 }, flushing = null;
          const flush = () => {
            flushing = null;
            if (pending.dx || pending.dy) send({ type: 'wheel', dx: pending.dx, dy: pending.dy });
            pending = { dx: 0, dy: 0 };
          };
          frame.addEventListener('pointermove', e => {
            if (e.pointerType === 'touch') {
              if (!touch || e.pointerId !== touch.id) return;
              if (!touch.scrolling && Math.hypot(e.clientX - touch.x, e.clientY - touch.y) > 10) {
                touch.scrolling = true;
                send({ type: 'mousemove', ...at({ clientX: touch.x, clientY: touch.y }) });
              }
              if (!touch.scrolling) return;
              const r = drawn(), scale = sent && r.width ? Math.min(1920, Math.max(320, sent.width)) / r.width : 1;
              pending.dx += (touch.lastX - e.clientX) * scale; pending.dy += (touch.lastY - e.clientY) * scale;
              touch.lastX = e.clientX; touch.lastY = e.clientY;
              if (!flushing) flushing = setTimeout(flush, 32);
              return;
            }
            const now = Date.now(); if (now - lastMove < 50) return; lastMove = now;
            send({ type: 'mousemove', ...at(e) });
          });
          frame.addEventListener('pointerup', e => {
            e.preventDefault();
            if (e.pointerType !== 'touch') { send({ type: 'mouseup', button: button(e), ...at(e) }); return; }
            if (!touch || e.pointerId !== touch.id) return;
            const tapped = !touch.scrolling; touch = null;
            if (!tapped) return;
            const p = at(e);
            if (!inside(p)) return;
            const field = fieldAt(p);
            if (field) { keyboardFor(field.kind); keys.focus(); focusOnTouchEnd = true; } else keys.blur();
            send({ type: 'mousedown', button: 'left', ...p }); send({ type: 'mouseup', button: 'left', ...p });
          });
          frame.addEventListener('pointercancel', () => { touch = null; });
          // Some phones raise the keyboard only for a focus made while their own touch event runs.
          frame.addEventListener('touchend', () => { if (focusOnTouchEnd) { focusOnTouchEnd = false; keys.focus(); } });
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
          document.querySelectorAll('[data-key]').forEach(b => {
            b.addEventListener('pointerdown', e => e.preventDefault());
            b.addEventListener('click', () => { send({ type: 'keydown', key: b.dataset.key }); send({ type: 'keyup', key: b.dataset.key }); });
          });

          // Where the person goes from here. An installed chat app has no Back button of its own.
          const backToChat = () => { if (history.length > 1) history.back(); else location.assign('/'); };
          const glyph = ok => ok
            ? '<svg aria-hidden="true" viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>'
            : '<svg aria-hidden="true" viewBox="0 0 24 24" width="26" height="26" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 17H7A5 5 0 0 1 7 7h2"/><path d="M15 7h2a5 5 0 1 1 0 10h-2"/><line x1="8" x2="16" y1="12" y2="12"/></svg>';
          const finish = (text, ok) => {
            finished = true; clearTimeout(retryTimer); if (ws) ws.close();
            document.body.innerHTML = '<div class="done"><div class="card"><div class="mark"></div><h1></h1><p></p></div></div>';
            const card = document.querySelector('.card');
            card.querySelector('.mark').innerHTML = glyph(ok);
            card.querySelector('h1').textContent = text[0];
            card.querySelector('p').textContent = text[1];
            const more = document.createElement('p');
            if (from === 'chat') {
              const back = document.createElement('button');
              back.type = 'button'; back.className = 'primary'; back.textContent = 'Back to the chat';
              back.addEventListener('click', backToChat);
              card.append(back);
              if (ok && history.length > 1) { more.textContent = 'Taking you back to the chat…'; card.insertBefore(more, back); setTimeout(backToChat, 1500); }
            } else if (from === 'telegram') {
              more.textContent = 'You can go back to Telegram now.'; card.append(more);
            } else if (history.length > 1) {
              const back = document.createElement('button');
              back.type = 'button'; back.className = 'primary'; back.textContent = 'Go back';
              back.addEventListener('click', () => history.back());
              card.append(back);
            } else {
              more.textContent = 'You can close this page.'; card.append(more);
            }
          };
          const ended = () => finish(['This login has ended', 'It ran out of time or the browser restarted. Ask again for a fresh link.'], false);

          done.addEventListener('click', async () => {
            done.disabled = true;
            const remember = document.getElementById('remember');
            let answer = null;
            try {
              answer = await fetch(pages + '/handoff/done', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ remember: remember ? remember.checked : true })
              });
            } catch {
              // Unreachable says nothing about the login: it stays open, and Done can be pressed again.
            }
            if (!answer) { done.disabled = false; say('The server could not be reached. Check the connection and press Done again.', true); return; }
            if (answer.ok) finish(['You are logged in', 'The agent is carrying on with what you asked.'], true);
            else finish(['This login has ended', 'Ask again for a fresh link.'], false);
          });

          const connect = () => {
            clearTimeout(retryTimer);
            if (finished || (ws && ws.readyState <= 1)) return;
            ws = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + pages + '/handoff/live');
            ws.binaryType = 'blob';
            ws.onopen = () => {
              retry = 0; dot.className = 'dot live';
              // Connecting again with the keyboard up must not shrink the site under the field: the last
              // size is said again unless the width changed meanwhile.
              if (sent && Math.abs(size().width - sent.width) <= 40) send({ type: 'resize', ...sent }); else sendSize();
            };
            ws.onmessage = e => {
              if (typeof e.data === 'string') {
                try { const m = JSON.parse(e.data); if (m.type === 'fields') fields = m.fields || []; } catch { }
                return;
              }
              const next = URL.createObjectURL(e.data);
              frame.onload = () => { if (url) URL.revokeObjectURL(url); url = next; say(''); };
              frame.src = next;
            };
            ws.onclose = () => { if (!finished) lost(); };
          };

          // A dropped view is not an ended login: a phone that left for its authenticator app comes back to
          // a closed socket. The server says whether the login is still open; while it is, connect again.
          const lost = async () => {
            dot.className = 'dot away';
            say('Reconnecting…');
            let answer = null;
            try { answer = await fetch(pages + '/handoff/state', { cache: 'no-store' }); } catch { }
            if (answer && answer.status === 401) { ended(); return; }
            if (answer && answer.ok && !(await answer.json()).open) { ended(); return; }
            retry = Math.min(retry + 1, 5);
            retryTimer = setTimeout(connect, document.hidden ? 15000 : 250 * 2 ** retry);
          };
          const comeBack = () => { if (claimed && !finished && (!ws || ws.readyState > 1)) connect(); };
          document.addEventListener('visibilitychange', () => { if (!document.hidden) comeBack(); });
          window.addEventListener('online', comeBack);
          window.addEventListener('pageshow', comeBack);

          const show = login => {
            from = login.from || null;
            document.getElementById('title').textContent = 'Log in to ' + login.site;
            document.title = 'Log in to ' + login.site;
            if (!login.canRemember) {
              const box = document.getElementById('remember');
              box.checked = false; box.disabled = true;
              document.getElementById('remember-note').hidden = false;
            }
            done.disabled = false;
            claimed = true;
            connect();
          };

          (async () => {
            let claim = null;
            try { claim = await fetch(pages + '/handoff/' + encodeURIComponent(token) + '/claim', { method: 'POST' }); } catch { }
            if (claim && claim.ok) { show(await claim.json()); return; }
            // The page opened again — reloaded, or brought back after the phone let it go — finds the login
            // this browser already claimed, rather than calling its spent link the end of it.
            let state = null;
            try { state = await fetch(pages + '/handoff/state', { cache: 'no-store' }); } catch { }
            const login = state && state.ok ? await state.json() : null;
            if (login && login.open) { show(login); return; }
            finish(['This link has been used or has run out', 'Each link opens once and for ' + minutes + ' minutes. Ask again and you will get a fresh one.'], false);
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
    public static readonly IReadOnlyList<string> ScriptHashes =
        [HashOf(HandoffScript), HashOf(LoginsScript), PreviousHandoffScript];

    // The handoff script of the release before the live view was fitted to a phone (2026-11-14): kept
    // for one release, so a kit a step behind still shows its live view. Drop it the release after.
    private const string PreviousHandoffScript = "sha256-Y7hV3j9w/r+t8eAVnE61+NkOsrMA74Xxs0lftpTbRG4=";

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