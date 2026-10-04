const { launchServer } = require('camoufox-js');

(async () => {
    const server = await launchServer({
        host: '0.0.0.0',
        port: 9377,
        ws_path: '/browser',
        headless: 'virtual',
        // A working uBlock Origin (1.73.0 through 1.75.1b1 alike) stalls every request in the
        // browser partway through a few parallel browses -- channels open and never reach DNS --
        // until a restart. Before camoufox-js 0.12 it was unpacked flat and never ran at all, which
        // is the browser web browsing was proven against; this keeps it that way on purpose.
        exclude_addons: ['UBO'],
    });
    console.log('Camoufox server listening at:', server.wsEndpoint());
})();
