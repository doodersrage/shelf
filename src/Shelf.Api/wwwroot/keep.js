// Keeps a book's e-book on this device for reading offline, with its highlights, and remembers which books are kept.
import * as marks from "./offline-marks.js";

const BOOKS = "shelf-books";
const LIST = "shelf-kept";

function list() {
  try {
    return JSON.parse(localStorage.getItem(LIST) || "[]");
  } catch {
    return [];
  }
}

function save(books) {
  try {
    localStorage.setItem(LIST, JSON.stringify(books));
  } catch {
    // Private windows may refuse storage; the book is still in the cache for this visit.
  }
}

export function supported() {
  return "caches" in window && "serviceWorker" in navigator;
}

export function isKept(id) {
  return list().some((book) => book.id === id);
}

export async function keep(id, title, author, type) {
  const response = await fetch(`/books/${id}/ebook/file`, { credentials: "same-origin" });
  if (!response.ok) {
    return false;
  }

  const cache = await caches.open(BOOKS);
  await cache.put(`/books/${id}/ebook/file`, response);
  await marks.download(id);
  save([...list().filter((book) => book.id !== id), { id, title, author, type, keptAt: new Date().toISOString() }]);
  return true;
}

const AUDIO = "shelf-kept-audio";

function audioList() {
  try {
    return JSON.parse(localStorage.getItem(AUDIO) || "[]");
  } catch {
    return [];
  }
}

function saveAudio(books) {
  try {
    localStorage.setItem(AUDIO, JSON.stringify(books));
  } catch {
    // Private windows may refuse storage.
  }
}

export function isAudioKept(id) {
  return audioList().some((book) => book.id === id);
}

// Keeps an audiobook on this device: what the player needs, every track, and the cover, so it plays with no
// connection at all. The page's progress line (data-keep-progress) says how far it has got.
export async function keepAudio(id) {
  const progress = (text) => {
    const line = document.querySelector("[data-keep-progress]");
    if (line) line.textContent = text;
  };
  const answer = await fetch(`/books/${id}/audio/plan`, { credentials: "same-origin" });
  if (!answer.ok) {
    return false;
  }

  const plan = await answer.clone().json();
  // Asks the browser not to clear a recording to make room; it may say no, and the book is kept all the same.
  await navigator.storage?.persist?.().catch(() => false);
  const cache = await caches.open(BOOKS);
  let size = 0;
  for (const [index, track] of plan.tracks.entries()) {
    const template = document.querySelector("[data-keep-progress]")?.dataset.template;
    progress(template ? template.replace("{0}", index + 1).replace("{1}", plan.tracks.length) : `${index + 1} / ${plan.tracks.length}`);
    const response = await fetch(track.url, { credentials: "same-origin" });
    if (!response.ok) {
      await forgetAudio(id);
      return false;
    }
    size += Number(response.headers.get("Content-Length") || 0);
    await cache.put(track.url, response);
  }

  await cache.put(`/books/${id}/audio/plan`, answer);
  if (plan.cover && plan.cover.startsWith("/")) {
    const cover = await fetch(plan.cover, { credentials: "same-origin" }).catch(() => null);
    if (cover?.ok) await cache.put(plan.cover, cover);
  }

  saveAudio([...audioList().filter((book) => book.id !== id), { id, title: plan.title, author: plan.author, size, keptAt: new Date().toISOString() }]);
  progress("");
  return true;
}

export async function forgetAudio(id) {
  const cache = await caches.open(BOOKS);
  for (const request of await cache.keys()) {
    const path = new URL(request.url).pathname;
    if (path === `/books/${id}/audio/plan` || path.startsWith(`/books/${id}/audio/tracks/`)) {
      await cache.delete(request);
    }
  }
  saveAudio(audioList().filter((book) => book.id !== id));
}

export async function forget(id) {
  const cache = await caches.open(BOOKS);
  await cache.delete(`/books/${id}/ebook/file`);
  save(list().filter((book) => book.id !== id));
  marks.forget(id);
  try {
    localStorage.removeItem(`shelf-place-${id}`);
  } catch {
  }
}
