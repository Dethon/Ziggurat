namespace Accounts.Pages;

// The pages' one script. WebAuthn takes and gives binary fields as ArrayBuffers, and the server's
// JSON carries them as base64url, so each crossing converts. It converts by hand rather than with
// PublicKeyCredential.parseCreationOptionsFromJSON, which older phone browsers lack.
public static class PasskeyScript
{
    public const string Source =
        """
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

          button.addEventListener('click', async () => {
            button.disabled = true;
            status.textContent = 'Waiting for your passkey…';
            try {
              const ceremonies = { enroll, add, 'sign-in': signIn };
              const done = await ceremonies[root.dataset.passkey]();
              if (!done.ok) {
                status.textContent = await errorOf(done);
                button.disabled = false;
                return;
              }
              status.textContent = 'Done.';
              location.assign((await done.json()).redirect);
            } catch (error) {
              status.textContent = error instanceof Refusal
                ? await errorOf(error.response)
                : error.name === 'NotAllowedError' ? 'Nothing was chosen. Try again when you are ready.' : 'That did not work: ' + error.message;
              button.disabled = false;
            }
          });
        })();
        """;
}