// The app shell is cached so the PWA opens offline and installs; API, auth and event responses never are.
const CACHE = 'harness-shell-v2';
const SHELL = ['/', '/style.css', '/app.js', '/manifest.webmanifest', '/icon.svg'];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(caches.keys()
    .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== self.location.origin) return;
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/auth/') || url.pathname.startsWith('/hooks/')) return;
  const shell = url.pathname === '/setup' ? '/' : url.pathname;
  if (!SHELL.includes(shell)) return;
  // Network first, so a daemon upgrade is picked up at once; the cache is the offline fallback.
  event.respondWith(fetch(event.request)
    .then((response) => {
      if (response.ok) caches.open(CACHE).then((cache) => cache.put(shell, response.clone()));
      return response;
    })
    .catch(() => caches.match(shell)));
});

// Web Push: the daemon sends { title, body, url, tag }. A notification only opens the app; approving still happens there,
// after the passkey step-up a service worker cannot perform.
self.addEventListener('push', (event) => {
  let message = { title: 'harness', body: '', url: '/', tag: undefined };
  try { message = { ...message, ...event.data.json() }; } catch { /* keep the default */ }
  event.waitUntil(self.registration.showNotification(message.title, {
    body: message.body, tag: message.tag, data: { url: message.url }, icon: '/icon.svg', badge: '/icon.svg',
  }));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = new URL(event.notification.data?.url ?? '/', self.location.origin).href;
  event.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((windows) => {
    for (const w of windows) if (new URL(w.url).origin === self.location.origin) return w.navigate(url).then((c) => c?.focus());
    return self.clients.openWindow(url);
  }));
});
