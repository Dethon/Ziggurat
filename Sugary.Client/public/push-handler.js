// Imported by the generated service worker (vite.config.ts, workbox.importScripts): a reminder the
// Sugary server pushed (a pen, her period), shown even while the app is open, since it is about a pen
// in a drawer or a day ahead and not about anything on screen. One tag per kind, so the day's reminder
// replaces the day before's. A tap opens its tab: an open app is told which (App.tsx), never reloaded,
// so a meal in progress keeps its photos.
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
      if (!open) {
        return self.clients.openWindow(url);
      }
      open.postMessage({ type: "open", url });
      return open.focus();
    }),
  );
});
