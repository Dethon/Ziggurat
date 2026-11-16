// One device for the life of the deployment. Camoufox draws a fresh identity on every launch
// unless it is handed one, and a site that sees a logged-in session arrive from a new machine
// after every restart challenges it or drops it. So the identity is drawn once, kept in the
// service's volume, and handed to every launch; anonymous and person contexts share it, and
// cookies, not fingerprints, are what separate them.
//
// Every per-identity draw the launch makes (fonts, voices, audio seed, WebGL parameters) is
// seeded from the fingerprint it was handed, so the same file presents the same device.
const fs = require('fs');
const path = require('path');
const { generateFingerprint } = require('@camoufox/camoufox/dist/fingerprints.js');

async function loadOrCreateFingerprint(file) {
    if (fs.existsSync(file)) {
        // A file that does not parse is a broken device, not a missing one: failing here keeps
        // the container down where a fresh draw would silently log every person out.
        return JSON.parse(fs.readFileSync(file, 'utf8'));
    }

    if (typeof generateFingerprint !== 'function') {
        throw new Error('@camoufox/camoufox no longer exports generateFingerprint from dist/fingerprints.js');
    }

    const fingerprint = await generateFingerprint({});
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, JSON.stringify(fingerprint));
    console.log('Generated and saved a new browser fingerprint at', file);
    return fingerprint;
}

module.exports = { loadOrCreateFingerprint };
