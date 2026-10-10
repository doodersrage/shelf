// The offline reader: the books kept on this device, read from the cache, with the place sent back later.
import * as pdfjs from "/lib/pdfjs/pdf.min.mjs";

pdfjs.GlobalWorkerOptions.workerSrc = "/lib/pdfjs/pdf.worker.min.mjs";

const BOOKS = "shelf-books";
const $ = (id) => document.getElementById(id);
let book = null;
let index = 0;
let count = 0;
let show = null;

function kept() {
  try {
    return JSON.parse(localStorage.getItem("shelf-kept") || "[]");
  } catch {
    return [];
  }
}

function place(id) {
  try {
    return JSON.parse(localStorage.getItem(`shelf-place-${id}`) || "null");
  } catch {
    return null;
  }
}

function remember(id, at) {
  try {
    localStorage.setItem(`shelf-place-${id}`, JSON.stringify({ at, waiting: true }));
  } catch {
  }
}

// Places read offline go back to the shelf once it answers again; the shelf keeps whichever place is further.
async function sendPlaces() {
  for (const item of kept()) {
    const saved = place(item.id);
    if (!saved?.waiting) {
      continue;
    }

    try {
      const response = await fetch(`/books/${item.id}/place`, {
        method: "PUT",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ebookChapter: saved.at }),
      });
      if (response.ok || response.status === 404) {
        localStorage.setItem(`shelf-place-${item.id}`, JSON.stringify({ at: saved.at, waiting: false }));
      }
    } catch {
      return;
    }
  }
}

function listBooks() {
  const books = kept();
  const list = $("kept");
  list.replaceChildren();
  $("nothing").hidden = books.length > 0;
  for (const item of books) {
    const card = document.createElement("li");
    card.className = "strip-card";
    const body = document.createElement("div");
    const title = document.createElement("p");
    title.className = "title";
    const link = document.createElement("a");
    link.href = `#book-${item.id}`;
    link.textContent = item.title;
    title.append(link);
    const by = document.createElement("p");
    by.className = "by";
    by.textContent = item.author;
    body.append(title, by);
    const cover = document.createElement("span");
    cover.className = "cover-frame";
    const spine = document.createElement("span");
    spine.className = "spine";
    spine.setAttribute("aria-hidden", "true");
    spine.textContent = item.title;
    cover.append(spine);
    card.append(cover, body);
    list.append(card);
  }
}

async function open(item) {
  const response = await (await caches.open(BOOKS)).match(`/books/${item.id}/ebook/file`);
  if (!response) {
    $("page").textContent = "This book's file is no longer on this device.";
    return;
  }

  book = item;
  index = place(item.id)?.at ?? 0;
  $("shelf").hidden = true;
  $("reader").hidden = false;
  $("title").textContent = item.title;
  const data = await response.arrayBuffer();
  show = item.type === "pdf" ? await pdfReader(data) : await epubReader(data);
  index = Math.min(Math.max(index, 0), count - 1);
  await turn(0);
}

async function turn(step) {
  index = Math.min(Math.max(index + step, 0), count - 1);
  $("previous").disabled = index === 0;
  $("next").disabled = index >= count - 1;
  $("where").textContent = `${book.type === "pdf" ? "Page" : "Chapter"} ${index + 1} of ${count}`;
  await show(index);
  remember(book.id, index);
  $("page").scrollTop = 0;
  window.scrollTo(0, 0);
}

async function pdfReader(data) {
  const doc = await pdfjs.getDocument({ data, isEvalSupported: false, enableXfa: false }).promise;
  count = doc.numPages;
  return async (at) => {
    const page = await doc.getPage(at + 1);
    const natural = page.getViewport({ scale: 1 });
    const width = Math.max(240, $("page").clientWidth - 16);
    const viewport = page.getViewport({ scale: (width / natural.width) * (Number($("size").value) / 100) });
    const canvas = document.createElement("canvas");
    const ratio = window.devicePixelRatio || 1;
    canvas.width = Math.floor(viewport.width * ratio);
    canvas.height = Math.floor(viewport.height * ratio);
    canvas.style.width = `${Math.floor(viewport.width)}px`;
    await page.render({ canvasContext: canvas.getContext("2d"), viewport, transform: ratio === 1 ? undefined : [ratio, 0, 0, ratio, 0, 0] }).promise;
    $("page").replaceChildren(canvas);
  };
}

// An EPUB is a zip: its container names the package, whose spine lists the chapters in order.
async function epubReader(data) {
  const zip = await JSZip.loadAsync(data);
  const parse = (text, type) => new DOMParser().parseFromString(text, type);
  const container = parse(await zip.file("META-INF/container.xml").async("string"), "application/xml");
  const packagePath = container.querySelector("rootfile").getAttribute("full-path");
  const folder = packagePath.includes("/") ? packagePath.slice(0, packagePath.lastIndexOf("/") + 1) : "";
  const opf = parse(await zip.file(packagePath).async("string"), "application/xml");
  const items = new Map([...opf.querySelectorAll("manifest > item")].map((item) => [item.getAttribute("id"), item.getAttribute("href")]));
  const chapters = [...opf.querySelectorAll("spine > itemref")].map((ref) => resolve(folder, items.get(ref.getAttribute("idref")) || "")).filter((path) => zip.file(path));
  count = chapters.length;
  const urls = [];

  return async (at) => {
    urls.splice(0).forEach((url) => URL.revokeObjectURL(url));
    const path = chapters[at];
    const base = path.includes("/") ? path.slice(0, path.lastIndexOf("/") + 1) : "";
    const html = await zip.file(path).async("string");
    let doc = parse(html, "application/xhtml+xml");
    if (doc.querySelector("parsererror")) {
      doc = parse(html, "text/html");
    }

    // Only the words and pictures come across: no scripts, styles, frames, or forms from the book.
    doc.querySelectorAll("script, style, link, iframe, object, embed, form, meta, base").forEach((element) => element.remove());
    for (const element of doc.querySelectorAll("*")) {
      for (const attribute of [...element.attributes]) {
        const name = attribute.name.toLowerCase();
        if (name.startsWith("on") || (/href|src/.test(name) && /^\s*javascript:/i.test(attribute.value))) {
          element.removeAttribute(attribute.name);
        }
      }
    }

    for (const image of doc.querySelectorAll("img[src], image")) {
      const source = image.getAttribute("src") || image.getAttribute("href") || image.getAttribute("xlink:href");
      const file = source ? zip.file(resolve(base, source)) : null;
      if (file) {
        const url = URL.createObjectURL(await file.async("blob"));
        urls.push(url);
        image.setAttribute(image.tagName.toLowerCase() === "img" ? "src" : "href", url);
      }
    }

    const page = $("page");
    page.style.fontSize = `${$("size").value}%`;
    page.replaceChildren(...[...(doc.body || doc.documentElement).childNodes].map((node) => document.importNode(node, true)));
  };
}

function resolve(base, relative) {
  const parts = (base + decodeURIComponent(relative.split("#")[0])).split("/");
  const out = [];
  for (const part of parts) {
    if (part === "..") {
      out.pop();
    } else if (part !== "." && part !== "") {
      out.push(part);
    }
  }

  return out.join("/");
}

function route() {
  const match = location.hash.match(/^#book-(\d+)$/);
  const item = match ? kept().find((entry) => entry.id === Number(match[1])) : null;
  if (item) {
    open(item);
  } else {
    $("reader").hidden = true;
    $("shelf").hidden = false;
    listBooks();
  }
}

function connection() {
  $("connection").textContent = navigator.onLine ? "Online again" : "Offline";
  if (navigator.onLine) {
    sendPlaces();
  }
}

$("previous").addEventListener("click", () => turn(-1));
$("next").addEventListener("click", () => turn(1));
$("size").addEventListener("change", () => turn(0));
$("back").addEventListener("click", (event) => {
  event.preventDefault();
  location.hash = "";
});
$("page").addEventListener("keydown", (event) => {
  if (event.key === "ArrowRight") turn(1);
  if (event.key === "ArrowLeft") turn(-1);
});
window.addEventListener("hashchange", route);
window.addEventListener("online", connection);
window.addEventListener("offline", connection);
connection();
route();
