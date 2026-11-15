self.addEventListener('push', event => {
    let data;
    try {
        data = event.data?.json();
    } catch {
        data = null;
    }
    data ??= { title: 'New message', body: '' };

    const spaceSlug = data.spaceSlug;
    let title = data.title;
    if (spaceSlug && spaceSlug !== 'default') {
        const spaceName = spaceSlug.replace(/-/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
        title = `${title} — ${spaceName}`;
    }

    const notificationUrl = data.url ?? '/';

    // A finished reply names its conversation beside the space's address; a tap opens the app on it
    // (LaunchLink reads the query). The address alone is what says whether the space is on screen.
    const openUrl = data.agentId && data.topicId
        ? `${notificationUrl}?agent=${encodeURIComponent(data.agentId)}&topic=${encodeURIComponent(data.topicId)}`
        : notificationUrl;

    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: false })
            .then(clients => {
                const isOnSpace = clients.some(
                    c => c.visibilityState === 'visible' && new URL(c.url).pathname === notificationUrl
                );
                if (isOnSpace) return;
                // One notification per conversation: a later reply in it replaces the earlier one.
                const options = {
                    body: data.body,
                    icon: '/icon.svg',
                    data: { url: openUrl }
                };
                if (data.topicId) {
                    options.tag = `topic:${data.topicId}`;
                    options.renotify = true;
                }
                return self.registration.showNotification(title, options);
            })
    );
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = event.notification.data?.url ?? '/';
    event.waitUntil(
        self.clients.matchAll({ type: 'window' }).then(clients => {
            const existing = clients.find(c => new URL(c.url).origin === self.location.origin);
            if (existing) return existing.navigate(url).then(c => c.focus());
            return self.clients.openWindow(url);
        })
    );
});
