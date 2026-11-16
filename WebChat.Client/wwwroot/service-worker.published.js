self.importScripts('./push-handler.js');

// Caution! Be sure you understand the caveats before publishing an application with
// offline support. See https://aka.ms/blazor-offline-considerations

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => {
    event.waitUntil(onActivate(event));
    // Not inside waitUntil: only a page's active worker may navigate it, and this one is active only
    // once activation has finished.
    reopenAccountsPages();
});
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff2?$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/];
const offlineAssetsExclude = [/^service-worker\.js$/];

// Pages on this host that are other services' — sign-in and a link's enrollment (the accounts
// service), a login handoff and the logins page (the browse server) — which the proxy routes past
// this app. A navigation there must reach the network: answered with the app's index.html, a
// signed-out installed app would boot, be sent to sign in, and be handed itself again.
const otherServicesPages = [/^\/accounts\//, /^\/browse\//];

async function onInstall(event) {
    console.info('Service worker: Install');

    // Fetch and cache all matching items from the assets manifest
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, {integrity: asset.hash, cache: 'no-cache'}));
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));

    // Activate immediately so new push handlers take effect without waiting for tab close
    self.skipWaiting();
}

async function onActivate(event) {
    console.info('Service worker: Activate');

    // Delete unused caches
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));

    // Take control of all open tabs immediately
    self.clients.claim();
}

// A worker from before sign-in answered every navigation with its cached app, the accounts service's
// pages included, so a passkey link opened where it was installed showed the old app's "nothing at
// this address". The navigation that did so is also the one that fetched this worker: such a page is
// sent on to what it asked for. Only the accounts service's pages — reloading one costs nothing,
// where a login handoff's page is a single-use link. Asked until this worker is the one the pages
// answer to, which navigating them needs: until it has claimed them, navigating one is refused.
async function reopenAccountsPages() {
    for (let attempt = 0; attempt < 40; attempt++) {
        await new Promise(resolve => setTimeout(resolve, 250));
        const pages = (await self.clients.matchAll({ type: 'window', includeUncontrolled: true }))
            .filter(client => /^\/accounts\//.test(new URL(client.url).pathname));
        const reopened = await Promise.all(pages.map(client => client.navigate(client.url).then(() => true, () => false)));
        if (reopened.every(Boolean)) {
            return;
        }
    }
}

async function onFetch(event) {
    let cachedResponse = null;
    if (event.request.method === 'GET') {
        // For the app's own navigations, try to serve index.html from cache
        const shouldServeIndexHtml = event.request.mode === 'navigate'
            && !otherServicesPages.some(pattern => pattern.test(new URL(event.request.url).pathname));
        const request = shouldServeIndexHtml ? 'index.html' : event.request;
        const cache = await caches.open(cacheName);
        cachedResponse = await cache.match(request);
    }

    return cachedResponse || fetch(event.request);
}
