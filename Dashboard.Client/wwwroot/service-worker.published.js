// Caution! Be sure you understand the caveats before publishing an application with
// offline support. See https://aka.ms/blazor-offline-considerations

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'dashboard-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.svg$/];
const offlineAssetsExclude = [/^service-worker\.js$/];

async function onInstall(event) {
    console.info('Service worker: Install');

    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, {integrity: asset.hash, cache: 'no-cache'}));
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));

    self.skipWaiting();
}

async function onActivate(event) {
    console.info('Service worker: Activate');

    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));

    self.clients.claim();
}

// A page being opened goes to the network first, and is answered from the cache only when the network
// cannot be reached: the dashboard is the operator's alone, and the proxy answers a page nobody is
// signed in to with sign-in on the main host. Answered from the cache, a signed-out page loaded the
// app, the app's hub was refused, and the reload it asked for was answered from the cache again.
async function onFetch(event) {
    const cache = await caches.open(cacheName);
    if (event.request.method === 'GET' && event.request.mode === 'navigate') {
        try {
            return await fetch(event.request);
        } catch {
            return (await cache.match('index.html')) || Response.error();
        }
    }

    const cachedResponse = event.request.method === 'GET' ? await cache.match(event.request) : null;
    return cachedResponse || fetch(event.request);
}
