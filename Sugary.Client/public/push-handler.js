// Imported by the generated service worker (vite.config.ts, workbox.importScripts): a pen reminder
// the Sugary server pushed, shown even while the app is open, since it is about a pen in a drawer
// and not about anything on screen. One tag per kind, so the day's reminder replaces the day before's.
self.addEventListener("push", (event) => {
  let data = null;
  try {
    data = event.data?.json() ?? null;
  } catch {
    data = null;
  }
  if (!data?.title) {
    return;
  }
  event.waitUntil(
    self.registration.showNotification(data.title, {
      body: data.body ?? "",
      tag: data.tag,
      renotify: Boolean(data.tag),
      icon: "/icon-192.png",
      badge: "/icon-192.png",
      data: { url: data.url ?? "/" },
    }),
  );
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const url = event.notification.data?.url ?? "/";
  event.waitUntil(
    self.clients.matchAll({ type: "window", includeUncontrolled: true }).then((clients) => {
      const open = clients.find((client) => new URL(client.url).origin === self.location.origin);
      return open ? open.focus() : self.clients.openWindow(url);
    }),
  );
});
