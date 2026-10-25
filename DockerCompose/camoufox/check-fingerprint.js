// Build-time guard for the pinned fingerprint: the package this image installs must still accept
// a fingerprint an earlier one saved, and must turn it into the same launch config every time.
// A bump that breaks either would have every deployment's saved device refused at start, or
// presented differently on each launch, so it fails the build here instead.
const fs = require('fs');
const zlib = require('zlib');
const { launchOptions } = require('@camoufox/camoufox');
const { generateFingerprint } = require('@camoufox/camoufox/dist/fingerprints.js');

function configOf(options) {
    return Object.entries(options.env)
        .filter(([key]) => key.startsWith('CAMOU_CONFIG'))
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([, value]) => value)
        .join('');
}

(async () => {
    if (typeof generateFingerprint !== 'function') {
        console.error('check-fingerprint: generateFingerprint is gone from dist/fingerprints.js; fingerprint.js cannot draw a device');
        process.exit(1);
    }

    // Saved by the package this image pinned when the pin was introduced.
    const saved = JSON.parse(zlib.gunzipSync(fs.readFileSync('fingerprint-fixture.json.gz')).toString('utf8'));
    let first, second;
    try {
        first = configOf(await launchOptions({ fingerprint: saved, headless: true }));
        second = configOf(await launchOptions({ fingerprint: saved, headless: true }));
    } catch (e) {
        console.error('check-fingerprint: this Camoufox package refuses a saved fingerprint:', e.message);
        process.exit(1);
    }

    const config = JSON.parse(first);
    if (first !== second || !config['navigator.userAgent'] || !config['screen.width']) {
        console.error('check-fingerprint: a saved fingerprint no longer presents one stable device');
        process.exit(1);
    }

    console.log('check-fingerprint: a saved fingerprint is accepted and stable');
})();
