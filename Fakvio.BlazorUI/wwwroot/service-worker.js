// Service worker for development — no caching, just a pass-through.
// During development we don't want stale cached assets hiding code changes.
// The published version (service-worker.published.js) enables offline-first caching.
//
// This file is referenced in index.html via navigator.serviceWorker.register().
// At publish time, the .NET SDK replaces it with service-worker.published.js.

self.addEventListener('install', event => event.waitUntil(self.skipWaiting()));
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', () => { /* In dev mode, let all requests pass through to the network */ });
