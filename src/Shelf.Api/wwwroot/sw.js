// Shelf's service worker. It keeps the offline reader and the books a reader chose to keep, and shows
// the offline reader when the shelf cannot be reached. Everything else goes to the network as usual.
const SHELL = "shelf-shell-v6";
const BOOKS = "shelf-books";
const SHELL_FILES = [
  "/offline.html",
  "/offline.js",
  "/offline-marks.js",
  "/listen.js",
  "/words.js",
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

  // A page that cannot load becomes the offline reader. A frame inside a page (a chapter of a book) is left alone:
  // the offline reader inside it would make no sense, and one cut short as its page changes is no failure.
  if (request.mode === "navigate") {
    if (request.destination === "document") {
      event.respondWith(fetch(request).catch(() => caches.match("/offline.html").then((page) => page || Response.error())));
    }
    return;
  }

  // A kept book's file is fetched as usual, and comes from the device when the shelf is out of reach.
  if (/^\/books\/\d+\/ebook\/file$/.test(url.pathname)) {
    event.respondWith(fetch(request).catch(() => caches.open(BOOKS).then((cache) => cache.match(url.pathname))));
    return;
  }

  // A kept audiobook's tracks play from the device, online or not; a player asks for a part of a track at a time,
  // so the kept copy is cut to the range asked for. Other books' tracks come from the shelf as usual.
  if (/^\/books\/\d+\/audio\/tracks\/\d+$/.test(url.pathname)) {
    event.respondWith(caches.open(BOOKS).then((cache) => cache.match(url.pathname)).then((kept) => (kept ? ranged(kept, request) : fetch(request))));
    return;
  }

  // What the player needs comes from the shelf, with the place as it is there, and from the device when it cannot.
  if (/^\/books\/\d+\/audio\/plan$/.test(url.pathname)) {
    event.respondWith(fetch(request).catch(() => caches.open(BOOKS).then((cache) => cache.match(url.pathname)).then((kept) => kept || Response.error())));
    return;
  }

  // The offline reader's own files come from the shelf while it answers, which also keeps the copies on the device
  // up to date, and from the device when it does not. From the device first, a new release's styles and words would
  // wait until this file itself changed.
  if (SHELL_FILES.includes(url.pathname)) {
    event.respondWith(
      fetch(request)
        .then((response) => {
          if (response.ok) {
            const copy = response.clone();
            event.waitUntil(caches.open(SHELL).then((cache) => cache.put(url.pathname, copy)));
          }
          return response;
        })
        .catch(() => caches.match(url.pathname).then((cached) => cached || Response.error())));
  }
});

// The bytes a player asked for, from a whole kept track: "bytes=100-", "bytes=100-199", or the last "bytes=-500".
async function ranged(kept, request) {
  const range = /^bytes=(\d*)-(\d*)$/.exec(request.headers.get("Range") || "");
  if (!range) {
    return kept;
  }

  const blob = await kept.blob();
  let start = range[1] === "" ? Math.max(0, blob.size - Number(range[2] || 0)) : Number(range[1]);
  let end = range[1] !== "" && range[2] !== "" ? Number(range[2]) : blob.size - 1;
  end = Math.min(end, blob.size - 1);
  if (start > end) {
    return new Response(null, { status: 416, headers: { "Content-Range": `bytes */${blob.size}` } });
  }

  return new Response(blob.slice(start, end + 1), {
    status: 206,
    headers: {
      "Content-Type": kept.headers.get("Content-Type") || "audio/mpeg",
      "Content-Range": `bytes ${start}-${end}/${blob.size}`,
      "Content-Length": String(end - start + 1),
      "Accept-Ranges": "bytes",
    },
  });
}
