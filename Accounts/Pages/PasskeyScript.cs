namespace Accounts.Pages;

// The pages' one script. WebAuthn takes and gives binary fields as ArrayBuffers, and the server's
// JSON carries them as base64url, so each crossing converts. It converts by hand rather than with
// PublicKeyCredential.parseCreationOptionsFromJSON, which older phone browsers lack.
public static class PasskeyScript
{
    public const string Source =
        """
        (() => {
          // A suspended household's notice: nothing pushes to a page the hub let go, so it asks for itself
          // again until the household is resumed, then goes back to where it was sent from.
          const notice = document.querySelector('[data-suspended]');
          if (!notice) return;
          const again = async () => {
            try {
              const answer = await fetch(location.href, {
                cache: 'no-store', credentials: 'same-origin', redirect: 'manual', headers: { Accept: 'text/html' }
              });
              if (answer.status !== 403) {
                location.replace(notice.dataset.return || location.href);
                return;
              }
            } catch {
              // Unreachable says nothing about the household; ask again later.
            }
            setTimeout(again, 20000);
          };
          setTimeout(again, 20000);
        })();
        (() => {
          const root = document.querySelector('[data-passkey]');
          if (!root) return;
          const button = document.getElementById('go');
          const status = document.getElementById('status');

          const decode = text => {
            const base64 = text.replace(/-/g, '+').replace(/_/g, '/');
            const padded = base64 + '='.repeat((4 - base64.length % 4) % 4);
            return Uint8Array.from(atob(padded), c => c.charCodeAt(0)).buffer;
          };
          const encode = buffer => btoa(String.fromCharCode(...new Uint8Array(buffer)))
            .replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
          const headers = { 'Content-Type': 'application/json' };
          if (root.dataset.antiforgery) headers.RequestVerificationToken = root.dataset.antiforgery;
          const post = (path, body) => fetch('/accounts/api/' + path, {
            method: 'POST',
            headers,
            body: JSON.stringify(body),
            credentials: 'same-origin'
          });
          const begin = async (path, body) => {
            const response = await post(path, body);
            if (!response.ok) throw new Refusal(response);
            return response.json();
          };
          class Refusal extends Error {
            constructor(response) { super('refused'); this.response = response; }
          }

          // Enrolling through a link and adding a passkey while signed in are the same ceremony,
          // begun and finished at different calls.
          const create = async (beginPath, beginBody, finishPath) => {
            const { ceremony, options } = await begin(beginPath, beginBody);
            options.challenge = decode(options.challenge);
            options.user.id = decode(options.user.id);
            options.excludeCredentials = (options.excludeCredentials || []).map(c => ({ ...c, id: decode(c.id) }));
            const credential = await navigator.credentials.create({ publicKey: options });
            return post(finishPath, {
              ceremony,
              credential: {
                id: credential.id,
                rawId: encode(credential.rawId),
                type: credential.type,
                response: {
                  attestationObject: encode(credential.response.attestationObject),
                  clientDataJSON: encode(credential.response.clientDataJSON),
                  transports: credential.response.getTransports ? credential.response.getTransports() : []
                },
                clientExtensionResults: credential.getClientExtensionResults()
              }
            });
          };
          const enroll = () => create('enroll/options', { token: root.dataset.token }, 'enroll');
          const add = () => create('passkeys/options', {}, 'passkeys');

          const signIn = async () => {
            const { ceremony, options } = await begin('sign-in/options', {});
            options.challenge = decode(options.challenge);
            options.allowCredentials = (options.allowCredentials || []).map(c => ({ ...c, id: decode(c.id) }));
            const credential = await navigator.credentials.get({ publicKey: options });
            return post('sign-in', {
              ceremony,
              returnUrl: root.dataset.returnUrl || null,
              credential: {
                id: credential.id,
                rawId: encode(credential.rawId),
                type: credential.type,
                response: {
                  authenticatorData: encode(credential.response.authenticatorData),
                  clientDataJSON: encode(credential.response.clientDataJSON),
                  signature: encode(credential.response.signature),
                  userHandle: credential.response.userHandle ? encode(credential.response.userHandle) : null
                },
                clientExtensionResults: credential.getClientExtensionResults()
              }
            });
          };

          const errorOf = async response => {
            try { return (await response.json()).error || 'That did not work.'; }
            catch { return 'That did not work.'; }
          };

          const label = button.innerHTML;
          const show = (text, kind) => {
            status.textContent = text;
            status.className = 'status' + (kind ? ' ' + kind : '');
          };
          const ready = () => {
            button.disabled = false;
            button.removeAttribute('aria-busy');
            button.innerHTML = label;
          };
          const busy = text => {
            button.disabled = true;
            button.setAttribute('aria-busy', 'true');
            show(text);
          };

          // What a person can do about a browser's refusal, in words; the browser's own message is not.
          const reason = error =>
            error.name === 'NotAllowedError' ? 'Nothing was chosen. Try again when you are ready.'
            : error.name === 'InvalidStateError' ? (root.dataset.passkey === 'enroll'
                ? 'This device already holds a passkey for this account, so there is nothing to add here: sign in instead.'
                : 'This device already holds a passkey for this account: add one from another device.')
            : error.name === 'NotSupportedError' || error.name === 'SecurityError' ? 'This browser cannot use passkeys here. Try your phone\'s own browser.'
            : 'That did not work: ' + error.message;

          // quiet: offered on its own when the page opened, so a prompt the browser refused to open
          // without a tap, or one dismissed, puts the button back without a word.
          const run = async quiet => {
            busy(root.dataset.passkey === 'sign-in' ? 'Waiting for your passkey…' : 'Waiting for this device…');
            try {
              const ceremonies = { enroll, add, 'sign-in': signIn };
              const done = await ceremonies[root.dataset.passkey]();
              if (!done.ok) {
                show(await errorOf(done), 'error');
                ready();
                return;
              }
              const { redirect } = await done.json();
              // The app is still loading when the page leaves, so the button keeps saying so until it has.
              button.textContent = root.dataset.passkey === 'sign-in' ? 'Signed in, opening…'
                : root.dataset.passkey === 'add' ? 'Passkey added' : 'Passkey created, opening…';
              show('');
              // The account page again by a GET, keeping its way back: it may have been drawn by a form's POST.
              if (root.dataset.passkey === 'add') location.assign('/accounts/account' + location.search);
              else location.assign(redirect);
            } catch (error) {
              ready();
              if (error instanceof Refusal) show(await errorOf(error.response), 'error');
              else if (quiet && error instanceof DOMException) show('');
              else show(reason(error), 'error');
              // A sign-in that went nowhere is most often a device with no passkey: what to do then is shown.
              const help = root.querySelector('details.help');
              if (help && status.textContent) help.open = true;
              if (error.name === 'InvalidStateError' && root.dataset.passkey === 'enroll') {
                const link = document.createElement('a');
                link.href = '/accounts/sign-in';
                link.textContent = 'Sign in';
                status.append(' ', link);
              }
            }
          };

          button.addEventListener('click', () => run(false));

          // An installed app opened signed out has nothing to do here but sign in, so the passkey is
          // offered at once. Not in a browser tab, where the page may have been opened to read it; not
          // after signing out (the page leaves data-autostart off); and once a minute at most, so a sign-in
          // that cannot stick never becomes a prompt that keeps coming back.
          const installed = matchMedia('(display-mode: standalone)').matches
            || matchMedia('(display-mode: fullscreen)').matches || navigator.standalone === true;
          if ('autostart' in root.dataset && installed && window.PublicKeyCredential) {
            let recent = false;
            try {
              recent = Date.now() - Number(sessionStorage.getItem('passkey-autostart') || 0) < 60000;
              sessionStorage.setItem('passkey-autostart', String(Date.now()));
            } catch {
              // No storage: offered anyway, once per page.
            }
            if (!recent) run(true);
          }
        })();
        (() => {
          // A form that takes something away says what before it does.
          document.addEventListener('submit', event => {
            const question = event.target.dataset && event.target.dataset.confirm;
            if (question && !confirm(question)) event.preventDefault();
          });
        })();
        """;
}