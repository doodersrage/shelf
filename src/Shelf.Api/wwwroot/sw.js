// Shelf's service worker. It keeps the offline reader and the books a reader chose to keep, and shows
// the offline reader when the shelf cannot be reached. Everything else goes to the network as usual.
const SHELL = "shelf-shell-v3";
const BOOKS = "shelf-books";
const SHELL_FILES = [
  "/offline.html",
  "/offline.js",
  "/offline-marks.js",
  "/app.css",
  "/favicon.svg",
  "/icon-192.png",
  "/lib/jszip/jszip.min.js",
  "/lib/pdfjs/pdf.min.mjs",
  "/lib/pdfjs/pdf.worker.min.mjs",
  "/fonts/inter-latin-wght-normal.woff2",
  "/fonts/lora-latin-wght-normal.woff2",
];

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(SHELL).then((cache) => cache.addAll(SHELL_FILES)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys()
      .then((names) => Promise.all(names.filter((name) => name.startsWith("shelf-shell-") && name !== SHELL).map((name) => caches.delete(name))))
      .then(() => self.clients.claim()));
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") {
    return;
  }

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) {
    return;
  }

  // A page that cannot load becomes the offline reader.
  if (request.mode === "navigate") {
    event.respondWith(fetch(request).catch(() => caches.match("/offline.html")));
    return;
  }

  // A kept book's file is fetched as usual, and comes from the device when the shelf is out of reach.
  if (/^\/books\/\d+\/ebook\/file$/.test(url.pathname)) {
    event.respondWith(fetch(request).catch(() => caches.open(BOOKS).then((cache) => cache.match(url.pathname))));
    return;
  }

  // The offline reader's own files come from the device first.
  if (SHELL_FILES.includes(url.pathname)) {
    event.respondWith(caches.match(url.pathname).then((cached) => cached || fetch(request)));
  }
});
