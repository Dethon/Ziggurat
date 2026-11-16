// Sends every screencast frame without waiting for the client's ack, which the .NET client never
// sends.
//
// playwright-core 1.63's page dispatcher hands each frame to the client as a screencastFrame event
// and awaits a screencastFrameAck carrying the frame's id before it acks the browser for the next
// one. Microsoft.Playwright 1.63 for .NET reads the event (Page -> Screencast.OnScreencastFrame)
// and never answers it, and its ScreencastFrame drops the frame id, so the ack cannot be sent from
// our side either. The first frame went out and the screencast stopped there: a login handoff's
// live view showed the page as it first loaded and nothing a person typed or clicked afterwards.
// Not waiting restores a stream; the live view keeps only the newest frame anyway
// (HandoffEndpoints), so nothing piles up.
const fs = require('fs');

const file = process.env.PW_CORE_BUNDLE || '/app/node_modules/playwright-core/lib/coreBundle.js';

const needle =
  '              const frameId = ++this._screencastFrameId;\n' +
  '              const promise = new ManualPromise();\n' +
  '              this._screencastFrameAcks.set(frameId, promise);\n' +
  '              this._dispatchEvent("screencastFrame", { frameId, data: frame.buffer, timestamp: frame.frameSwapWallTime, viewportWidth: frame.viewportWidth, viewportHeight: frame.viewportHeight });\n' +
  '              await promise;\n';
const replacement =
  '              const frameId = ++this._screencastFrameId;\n' +
  '              this._dispatchEvent("screencastFrame", { frameId, data: frame.buffer, timestamp: frame.frameSwapWallTime, viewportWidth: frame.viewportWidth, viewportHeight: frame.viewportHeight });\n';

let src = fs.readFileSync(file, 'utf8');
if (!src.includes(needle)) {
  console.error(
    'patch-screencast-ack: anchor not found — playwright-core internals changed. Re-verify whether the ' +
    '.NET client now acks screencast frames (Microsoft.Playwright Screencast.OnScreencastFrame) and drop this patch if so.');
  process.exit(1);
}
fs.writeFileSync(file, src.split(needle).join(replacement));
console.log('patch-screencast-ack: screencast frames no longer wait for a client ack.');
