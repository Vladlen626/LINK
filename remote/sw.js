// Link's remote page as an installed app. Nothing is cached: the tasks live in Link, and an old page would not match it.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => event.respondWith(fetch(event.request)));

// Link's notification: a task needs the user or is through. A tap opens the page, or brings an open one forward.
self.addEventListener('push', event => {
  const message = event.data ? event.data.json() : { title: 'Link', body: '' };
  event.waitUntil(self.registration.showNotification(message.title, { body: message.body, icon: '/icon.png', badge: '/icon.png', tag: 'link' }));
});

self.addEventListener('notificationclick', event => {
  event.notification.close();
  event.waitUntil(self.clients.matchAll({ type: 'window' }).then(windows =>
    windows.length > 0 ? windows[0].focus() : self.clients.openWindow('/')));
});
