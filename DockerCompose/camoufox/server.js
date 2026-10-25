const { launchServer } = require('@camoufox/camoufox');
const { loadOrCreateFingerprint } = require('./fingerprint');

// In the service's volume, so it outlives the container.
const fingerprintFile = process.env.CAMOUFOX_FINGERPRINT_FILE || '/data/fingerprint.json';

(async () => {
    const fingerprint = await loadOrCreateFingerprint(fingerprintFile);
    const server = await launchServer({
        host: '0.0.0.0',
        port: 9377,
        ws_path: '/browser',
        // camoufox-js turned 'virtual' into true in server mode; here 'virtual' would mean a
        // headed browser on Xvfb, which nothing has been proven against.
        headless: true,
        // The same device on every launch (see fingerprint.js).
        fingerprint,
        // uBlock Origin is installed by default. Before 156.0.1-beta.32 it deadlocked the browser
        // (daijro/camoufox#185), and before camoufox-js 0.12 it was unpacked flat and never ran,
        // so this is the first browser here that browses with a blocker.
    });
    console.log('Camoufox server listening at:', server.wsEndpoint());
})().catch(e => {
    console.error('Camoufox failed to start:', e);
    process.exit(1);
});
