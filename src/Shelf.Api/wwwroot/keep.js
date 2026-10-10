// Keeps a book's e-book on this device for reading offline, and remembers which books are kept.
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
  save([...list().filter((book) => book.id !== id), { id, title, author, type, keptAt: new Date().toISOString() }]);
  return true;
}

export async function forget(id) {
  const cache = await caches.open(BOOKS);
  await cache.delete(`/books/${id}/ebook/file`);
  save(list().filter((book) => book.id !== id));
  try {
    localStorage.removeItem(`shelf-place-${id}`);
  } catch {
  }
}
