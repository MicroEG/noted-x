'use strict';

// noted-x app shell cache. API, hub, and pairing requests always go to the
// network; only the interface is cached so the app opens instantly and shows
// its offline state when the desktop is unreachable.

const VERSION = 'noted-x-v2';
const STATIC_CACHE = `${VERSION}-static`;
const RUNTIME_CACHE = `${VERSION}-runtime`;

const PRECACHE = [
    '/',
    '/index.html',
    '/app.js',
    '/styles.css',
    '/manifest.json',
    '/vendor/signalr.min.js',
    '/icons/icon-180.png',
    '/icons/icon-192.png',
    '/icons/icon-512.png',
    '/icons/icon-maskable-512.png',
    '/icons/favicon.ico',
    '/icons/logo.png',
    '/icons/mark.png'
];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(STATIC_CACHE)
            .then((cache) => cache.addAll(PRECACHE))
            .then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        const keys = await caches.keys();
        await Promise.all(
            keys
                .filter((key) => !key.startsWith(VERSION))
                .map((key) => caches.delete(key)));
        await self.clients.claim();
    })());
});

self.addEventListener('message', (event) => {
    if (event.data === 'SKIP_WAITING') {
        self.skipWaiting();
    }
});

self.addEventListener('fetch', (event) => {
    const request = event.request;

    if (request.method !== 'GET') {
        return;
    }

    let url;
    try {
        url = new URL(request.url);
    } catch {
        return;
    }

    if (url.origin !== self.location.origin) {
        return;
    }

    if (url.pathname.startsWith('/api/') ||
        url.pathname === '/hub' ||
        url.pathname.startsWith('/hub/') ||
        request.headers.has('authorization')) {
        return;
    }

    if (request.mode === 'navigate') {
        event.respondWith(networkFirst(request));
        return;
    }

    event.respondWith(staleWhileRevalidate(request));
});

async function networkFirst(request) {
    try {
        return await fetch(request);
    } catch {
        const cached = await caches.match('/index.html') || await caches.match('/');
        return cached || new Response('The app shell is not cached yet.', {
            status: 503,
            headers: { 'Content-Type': 'text/plain; charset=utf-8' }
        });
    }
}

async function staleWhileRevalidate(request) {
    const cached = await caches.match(request);
    const network = fetch(request)
        .then((response) => {
            if (response && response.ok && response.type === 'basic') {
                const copy = response.clone();
                caches.open(RUNTIME_CACHE).then((cache) => cache.put(request, copy)).catch(() => {});
            }

            return response;
        })
        .catch(() => null);

    if (cached) {
        return cached;
    }

    const response = await network;
    return response || new Response('Offline', {
        status: 503,
        statusText: 'Offline',
        headers: { 'Content-Type': 'text/plain; charset=utf-8' }
    });
}
