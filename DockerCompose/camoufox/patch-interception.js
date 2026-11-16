// Strips the juggler request-interception field that playwright-core 1.64 added but the
// Camoufox Firefox build's juggler does not describe in its protocol scheme.
//
// playwright-core 1.64 started sending `bypassServiceWorker` beside `enabled` on
// Network.setRequestInterception (true for the page that restores a context's storage state).
// Camoufox's juggler declares only `enabled`, and its Dispatcher validates params strictly, so
// every route and every context made with storage state failed with:
//   Found property "<root>.bypassServiceWorker" - true which is not described in this scheme
// Sending `enabled` alone restores the exact 1.63 wire shape, which Camoufox accepts.
const fs = require('fs');

const file = process.env.PW_CORE_BUNDLE || '/app/node_modules/playwright-core/lib/coreBundle.js';

const needle =
  '          this._session.send("Network.setRequestInterception", { enabled, bypassServiceWorker }),';
const replacement =
  '          this._session.send("Network.setRequestInterception", { enabled }),';

const src = fs.readFileSync(file, 'utf8');
if (!src.includes(needle)) {
  console.error(
    'patch-interception: anchor not found for Network.setRequestInterception — playwright-core ' +
    'internals changed. Re-verify whether Camoufox\'s juggler now describes bypassServiceWorker ' +
    'and update this guard.');
  process.exit(1);
}
fs.writeFileSync(file, src.split(needle).join(replacement));
console.log('patch-interception: stripped bypassServiceWorker from juggler request interception.');
