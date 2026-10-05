const { launchServer } = require('@camoufox/camoufox');

(async () => {
    const server = await launchServer({
        host: '0.0.0.0',
        port: 9377,
        ws_path: '/browser',
        // camoufox-js turned 'virtual' into true in server mode; here 'virtual' would mean a
        // headed browser on Xvfb, which nothing has been proven against.
        headless: true,
        // uBlock Origin is installed by default. Before 156.0.1-beta.32 it deadlocked the browser
        // (daijro/camoufox#185), and before camoufox-js 0.12 it was unpacked flat and never ran,
        // so this is the first browser here that browses with a blocker.
    });
    console.log('Camoufox server listening at:', server.wsEndpoint());
})();
